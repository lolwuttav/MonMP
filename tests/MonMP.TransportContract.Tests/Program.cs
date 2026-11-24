using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.Transport;

internal static class Program
{
    private static int passed, failed;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Until(Func<bool> condition, Action poll, int timeout = 7000)
    {
        var watch = Stopwatch.StartNew();
        do { poll(); if (condition()) return; Thread.Sleep(1); } while (watch.ElapsedMilliseconds < timeout);
        throw new TimeoutException("Transport condition was not met.");
    }
    private static SessionOptions Options(string name) => new SessionOptions
    { Scene = "TransportContract", DisplayName = name, EnableIPv6 = false };
    private static byte[] Message(MessageKind kind, int marker, int length)
    {
        var bytes = new byte[length];
        bytes[6] = (byte)kind;
        BitConverter.GetBytes(marker).CopyTo(bytes, 7);
        for (int i = 23; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + marker);
        return bytes;
    }

    private static void BoundsAndChannels()
    {
        foreach (int size in new[] { 0, 22, 1025 })
            Throws<ArgumentException>(() => TransportMessageRules.ValidateOutgoing(new byte[size]));
        Throws<ArgumentException>(() => TransportMessageRules.ValidateOutgoing(null!));
        foreach (int size in new[] { 23, 1024 })
        {
            var world = Message(MessageKind.WorldDelta, 1, size);
            var media = Message(MessageKind.CdMedia, 2, size);
            TransportMessageRules.ValidateOutgoing(world);
            Check(TransportMessageRules.ChannelFor(world, true) == 0, "World channel changed.");
            Check(TransportMessageRules.ChannelFor(media, true) == 2, "Media lost its independent stream.");
            Check(TransportMessageRules.ChannelFor(media, false) == 0, "Unreliable messages gained a reliable channel.");
            Check(TransportMessageRules.ValidIncoming(world, true, 0), "Valid world message rejected.");
            Check(TransportMessageRules.ValidIncoming(media, true, 2), "Valid media message rejected.");
            Check(!TransportMessageRules.ValidIncoming(world, true, 2), "World message admitted on media stream.");
            Check(!TransportMessageRules.ValidIncoming(media, true, 0), "Media admitted on world stream.");
            Check(!TransportMessageRules.ValidIncoming(world, true, 1), "Unsupported reliable stream admitted.");
        }
        Check(!TransportMessageRules.ValidIncoming(new byte[1025], false, 0), "Oversized datagram admitted.");
        Check(!TransportMessageRules.ValidIncoming(new byte[22], true, 0), "Truncated envelope admitted.");
        Check(!TransportMessageRules.ValidIncoming(Message(MessageKind.Pose, 1, 23), false, 2), "Unexpected datagram lane admitted.");
    }

    private static void UdpPreservesBoundariesAndOrderedStreams()
    {
        using var host = new LiteNetTransport(true, false);
        using var guest = new LiteNetTransport(false, false);
        int hostConnection = -1, guestConnection = -1;
        host.Connected += id => hostConnection = id;
        guest.Connected += id => guestConnection = id;
        host.Start(0); guest.Start(0); guest.Connect("127.0.0.1", host.LocalPort);
        void Poll() { host.Poll(); guest.Poll(); }
        Until(() => hostConnection >= 0 && guestConnection >= 0, Poll);
        var world = new List<int>(); var media = new List<int>(); var poses = new HashSet<int>();
        int malformed = 0;
        guest.Received += (_, bytes, reliable) =>
        {
            int marker = BitConverter.ToInt32(bytes, 7);
            var kind = (MessageKind)bytes[6];
            int expectedLength = kind == MessageKind.Pose ? 96 : marker % 2 == 0 ? 1024 : 23;
            if (!bytes.SequenceEqual(Message(kind, marker, expectedLength))) malformed++;
            if (kind == MessageKind.WorldDelta && reliable) world.Add(marker);
            else if (kind == MessageKind.CdMedia && reliable) media.Add(marker);
            else if (kind == MessageKind.Pose && !reliable) poses.Add(marker);
            else malformed++;
        };
        // The 1024-byte messages exercise the existing LiteNetLib fragmentation
        // path when the negotiated MTU requires it; no application fragmentation.
        for (int i = 0; i < 80; i++)
        {
            host.Send(hostConnection, Message(MessageKind.WorldDelta, i, i % 2 == 0 ? 1024 : 23), true);
            host.Send(hostConnection, Message(MessageKind.CdMedia, i, i % 2 == 0 ? 1024 : 23), true);
            if (i < 20) host.Send(hostConnection, Message(MessageKind.Pose, i, 96), false);
        }
        Until(() => world.Count == 80 && media.Count == 80 && poses.Count == 20, Poll);
        Check(malformed == 0, "Payload boundaries, bytes, or delivery mode changed.");
        Check(world.SequenceEqual(Enumerable.Range(0, 80)) && media.SequenceEqual(Enumerable.Range(0, 80)), "Ordered delivery changed.");
        Check(host.Metrics(hostConnection).Route == "Direct UDP" && host.Metrics(hostConnection).RemoteIdentity.Length > 0,
            "Direct route diagnostics missing.");
        Throws<ArgumentException>(() => host.Send(hostConnection, new byte[1025], true));
    }

    private static void UdpUnreachableHostReportsConnectionFailed()
    {
        // LiteNetLib reports a failed outgoing connect without a peer; the guest must still
        // receive ConnectionFailed (shown as "could not reach") instead of the handler throwing.
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var guest = new LiteNetTransport(false, false);
        string? reason = null; int connection = int.MinValue;
        guest.Disconnected += (id, text) => { connection = id; reason = text; };
        guest.Start(0); guest.Connect("127.0.0.1", port);
        Until(() => reason != null, guest.Poll, 20000);
        Check(reason!.Contains("ConnectionFailed"), "Unreachable host reported " + reason + " instead of ConnectionFailed.");
    }
    private static void UdpUnresolvableAddressReportsUnknownHost()
    {
        // LiteNetLib raises this disconnect with a null peer before sending anything (0.9.77
        // report: NullReferenceException in the handler, "Session stopped after an error").
        using var guest = new LiteNetTransport(false, false, 0, "monmp-unresolvable.invalid", 7777);
        string? reason = null;
        guest.Disconnected += (_, text) => reason = text;
        guest.Start();
        Until(() => reason != null, guest.Poll, 20000);
        Check(reason == "UnknownHost", "Unresolvable address reported " + reason + " instead of UnknownHost.");
    }
    private static void UdpDisposesFromPollWithoutFurtherCallbacks()
    {
        using var host = new LiteNetTransport(true, false);
        using var guest = new LiteNetTransport(false, false);
        int hostConnection = -1, guestConnection = -1, received = 0;
        host.Connected += id => hostConnection = id;
        guest.Connected += id => guestConnection = id;
        host.Start(0); guest.Start(0); guest.Connect("127.0.0.1", host.LocalPort);
        void Poll() { host.Poll(); guest.Poll(); }
        Until(() => hostConnection >= 0 && guestConnection >= 0, Poll);
        guest.Received += (_, _, _) => { received++; guest.Dispose(); };
        for (int i = 0; i < 20; i++) host.Send(hostConnection, Message(MessageKind.WorldDelta, i, 23), true);
        Until(() => received > 0, Poll);
        for (int i = 0; i < 20; i++) Poll();
        Check(received == 1, "Disposed adapter delivered a stale callback from the same Poll.");
        guest.Dispose();
        Throws<ObjectDisposedException>(() => guest.Start());
    }

    // This adapter exercises shared protocol/session ownership without Steam,
    // sockets, Unity, or native callbacks. It is explicitly not a Steam test.
    private sealed class FixtureTransport : INetworkTransport
    {
        private readonly FixtureTransport? host;
        private readonly Queue<Action> pending = new Queue<Action>();
        private readonly Dictionary<int, (FixtureTransport Other, int Id)> links = new Dictionary<int, (FixtureTransport, int)>();
        public event Action<int>? Connected;
        public event Action<int, string>? Disconnected;
        public event Action<int, byte[], bool>? Received;
        public event Action<string>? Error;
        public event Action<string>? Failed;
        public readonly List<(byte[] Bytes, bool Reliable)> Sent = new List<(byte[], bool)>();
        public Action? Starting;
        public bool FailStart, Disposed;
        public int Disposals, NextId = 731;
        public TransportKind Kind => TransportKind.Steam;
        public string Description => "in-memory contract fixture";
        public int LocalPort => 0;
        public long BytesSentPerSecond => 0;
        public long BytesReceivedPerSecond => 0;
        public long ReliableQueueOverflowDisconnects => 0;
        public int SubscriberCount => (Connected?.GetInvocationList().Length ?? 0) +
            (Disconnected?.GetInvocationList().Length ?? 0) + (Received?.GetInvocationList().Length ?? 0) +
            (Error?.GetInvocationList().Length ?? 0) + (Failed?.GetInvocationList().Length ?? 0);
        public FixtureTransport(FixtureTransport? host = null) { this.host = host; }
        public void Start()
        {
            Starting?.Invoke();
            if (FailStart) throw new IOException("Deliberate startup failure.");
            if (host == null) return;
            int localId = 917, remoteId = host.NextId++;
            links.Add(localId, (host, remoteId)); host.links.Add(remoteId, (this, localId));
            pending.Enqueue(() => Connected?.Invoke(localId));
            host.pending.Enqueue(() => host.Connected?.Invoke(remoteId));
        }
        public void Poll() { while (!Disposed && pending.Count != 0) pending.Dequeue()(); }
        public int Ping(int connection) => 0;
        public TransportPeerMetrics Metrics(int connection) => new TransportPeerMetrics(0, 0, 0, 0, "Fixture", "fixture:" + connection);
        public void Send(int connection, byte[] bytes, bool reliable)
        {
            TransportMessageRules.ValidateOutgoing(bytes);
            if (!links.TryGetValue(connection, out var link)) return;
            var copy = (byte[])bytes.Clone(); Sent.Add((copy, reliable));
            link.Other.pending.Enqueue(() =>
            { if (link.Other.links.ContainsKey(link.Id)) link.Other.Received?.Invoke(link.Id, copy, reliable); });
        }
        public void Disconnect(int connection, string reason)
        {
            if (!links.Remove(connection, out var link)) return;
            link.Other.links.Remove(link.Id);
            pending.Enqueue(() => Disconnected?.Invoke(connection, reason));
            link.Other.pending.Enqueue(() => link.Other.Disconnected?.Invoke(link.Id, reason));
        }
        public void Dispose()
        {
            if (Disposed) return;
            Disposals++; Disposed = true;
            foreach (int id in links.Keys.ToArray()) Disconnect(id, "Fixture closed");
            pending.Clear();
        }
        public void EmitRetiredCallbacks()
        {
            Connected?.Invoke(99); Disconnected?.Invoke(99, "stale");
            Received?.Invoke(99, Message(MessageKind.WorldDelta, 1, 23), true); Error?.Invoke("stale");
            Failed?.Invoke("stale failure");
        }
        public void FailAsynchronously(string reason) => pending.Enqueue(() => Failed?.Invoke(reason));
        public void ReportAsynchronously(string reason) => pending.Enqueue(() => Error?.Invoke(reason));
    }

    private static void StartupFailuresAndRetiredCallbacks()
    {
        using var host = new NetworkSession(Options("Contract host"));
        var failedHost = new FixtureTransport { FailStart = true, Starting = () => Check(host.IsHost, "Host state not initialized before Start.") };
        Throws<IOException>(() => host.Host(failedHost));
        Check(host.State == SessionState.Offline && host.Peers.Count == 0 && failedHost.Disposals == 1 && failedHost.SubscriberCount == 0,
            "Failed host retained adapter, callbacks, or peers.");
        var live = new FixtureTransport(); host.Host(live); host.Stop("Stopped cleanly");
        live.EmitRetiredCallbacks();
        Check(host.Status == "Stopped cleanly" && host.State == SessionState.Offline && live.SubscriberCount == 0,
            "Retired callbacks mutated the stopped session.");
        using var guest = new NetworkSession(Options("Contract guest"));
        var failedGuest = new FixtureTransport { FailStart = true, Starting = () => Check(guest.State == SessionState.Connecting, "Join state not initialized before Start.") };
        Throws<IOException>(() => guest.Join(failedGuest));
        Check(guest.State == SessionState.Offline && failedGuest.Disposals == 1 && failedGuest.SubscriberCount == 0,
            "Failed join retained its adapter.");
    }

    private static void SharedProtocolKeepsIdentityAndAuthority()
    {
        using var host = new NetworkSession(Options("Shared host"));
        using var a = new NetworkSession(Options("Shared guest A"));
        using var b = new NetworkSession(Options("Shared guest B"));
        var ht = new FixtureTransport(); var at = new FixtureTransport(ht); var bt = new FixtureTransport(ht);
        host.Host(ht); a.Join(at);
        void Poll() { host.Tick(); a.Tick(); b.Tick(); }
        Until(() => a.IsReady && host.Peers.Count == 2, Poll);
        b.Join(bt);
        Until(() => b.IsReady && a.Peers.Count == 3 && host.Peers.Count == 3, Poll);
        Check(a.LocalPeerId == 1 && b.LocalPeerId == 2 && a.Epoch == host.Epoch && b.Epoch == host.Epoch,
            "Transport IDs leaked into host-assigned player IDs or epoch.");
        Check(host.ActiveTransport == TransportKind.Steam && host.Port == 0, "Session lost adapter metadata.");
        int poses = 0, chats = 0;
        host.PoseReceived += (id, _) => { if (id == a.LocalPeerId) poses++; };
        b.ChatReceived += chat => { if (chat.PeerId == a.LocalPeerId) chats++; };
        a.SendPose(new PlayerPose(1, 2, 3, 4, 5)); Check(a.SendChat("contract chat"), "Chat was not accepted.");
        Until(() => poses == 1 && chats == 1, Poll);
        Check(at.Sent.Any(p => p.Bytes[6] == (byte)MessageKind.Pose && !p.Reliable) &&
            at.Sent.Any(p => p.Bytes[6] == (byte)MessageKind.Chat && p.Reliable), "Delivery semantics changed above the adapter.");
        // A validly encoded pose with another player's ID still fails host authority.
        at.Send(917, Wire.Encode(MessageKind.Pose, host.Epoch, writer =>
        {
            writer.Write(b.LocalPeerId); writer.Write(99u); writer.Write(1.0);
            new PlayerPose(1, 2, 3, 4, 5).Write(writer);
        }), false);
        Until(() => a.State == SessionState.Offline && host.Peers.Count == 2 && b.Peers.Count == 2, Poll);
        Check(host.LastPacketError.Contains("identity spoofing", StringComparison.Ordinal), "Host authority was bypassed.");
        host.Stop();
        Until(() => b.State == SessionState.Offline, Poll);
        Check(ht.Disposals == 1 && at.Disposals == 1 && bt.Disposals == 1, "Host departure did not retire adapters.");
    }

    private static void TerminalAsynchronousFailureRetiresBothRoles()
    {
        using var host = new NetworkSession(Options("Failure host"));
        var failedHost = new FixtureTransport();
        host.Host(failedHost);
        failedHost.ReportAsynchronously("Nonfatal diagnostic"); host.Tick();
        Check(host.IsHost && host.Status == "Nonfatal diagnostic", "Nonfatal Error closed hosting.");
        failedHost.FailAsynchronously("Lobby creation failed"); host.Tick();
        Check(host.State == SessionState.Offline && host.Status == "Lobby creation failed" &&
            failedHost.Disposals == 1 && failedHost.SubscriberCount == 0 && host.Peers.Count == 0,
            "Async host failure retained a hosting session or adapter.");
        var replacementHost = new FixtureTransport(); host.Host(replacementHost);
        using var guest = new NetworkSession(Options("Failure guest"));
        var failedGuest = new FixtureTransport(); guest.Join(failedGuest);
        failedGuest.FailAsynchronously("Lobby join failed"); guest.Tick();
        Check(guest.State == SessionState.Offline && guest.Status == "Lobby join failed" &&
            failedGuest.Disposals == 1 && failedGuest.SubscriberCount == 0,
            "Async join failure retained its adapter.");
        var replacementGuest = new FixtureTransport(replacementHost); guest.Join(replacementGuest);
        Until(() => guest.IsReady && host.Peers.Count == 2, () => { host.Tick(); guest.Tick(); });
        replacementHost.FailAsynchronously("Steam service lost"); host.Tick(); guest.Tick();
        Check(host.State == SessionState.Offline && guest.State == SessionState.Offline &&
            replacementHost.Disposals == 1 && replacementGuest.Disposals == 1,
            "Terminal failure of a connected host did not clean up both endpoints.");
    }

    private static void ActualDirectRepeatedSessionsLateJoinReconnectAndDeparture()
    {
        using var host = new NetworkSession(Options("Cycle host"));
        using var a = new NetworkSession(Options("Cycle guest A"));
        using var b = new NetworkSession(Options("Cycle guest B"));
        void Poll() { host.Tick(); a.Tick(); b.Tick(); }
        for (int cycle = 0; cycle < 3; cycle++)
        {
            host.Host(0); a.Join("127.0.0.1", host.Port);
            Until(() => a.IsReady && host.Peers.Count == 2, Poll);
            b.Join("127.0.0.1", host.Port);
            Until(() => b.IsReady && host.Peers.Count == 3 && a.Peers.Count == 3, Poll);
            uint id = a.LocalPeerId;
            a.Stop(); Until(() => host.Peers.Count == 2 && b.Peers.Count == 2, Poll);
            a.Join("127.0.0.1", host.Port);
            Until(() => a.IsReady && host.Peers.Count == 3 && b.Peers.Count == 3, Poll);
            Check(a.LocalPeerId != id, "Reconnection reused a stale transport/player route.");
            Check(host.ActiveTransport == TransportKind.Direct && a.ActiveTransport == TransportKind.Direct,
                "Legacy Direct entry point selected another transport.");
            host.Stop(); Until(() => a.State == SessionState.Offline && b.State == SessionState.Offline, Poll);
            Check(host.Peers.Count == 0 && a.Peers.Count == 0 && b.Peers.Count == 0, "Session cleanup retained membership.");
        }
    }

    private static void DisconnectReasonsKeepUtf8Boundaries()
    {
        var utf8 = new UTF8Encoding(false, true);
        foreach (var sample in new[]
        {
            (Reason: new string('x', 200), Expected: new string('x', 200)),
            (Reason: new string('x', 201), Expected: new string('x', 200)),
            (Reason: new string('x', 256), Expected: new string('x', 200)),
            (Reason: new string('x', 198) + "é" + "suffix", Expected: new string('x', 198) + "é"),
            (Reason: new string('x', 199) + "é" + "suffix", Expected: new string('x', 199)),
            (Reason: new string('x', 197) + "😀" + "suffix", Expected: new string('x', 197)),
            (Reason: new string('x', 196) + "😀" + "suffix", Expected: new string('x', 196) + "😀")
        })
        {
            string result = TransportDisconnectReasons.Normalize(sample.Reason);
            Check(result == sample.Expected && utf8.GetByteCount(result) <= 200,
                "Disconnect reason changed its prefix or split a Unicode scalar value.");
            using var packet = Wire.Decode(Wire.Encode(MessageKind.Reject, Guid.Empty,
                writer => Wire.WriteText(writer, result, TransportDisconnectReasons.MaximumUtf8Bytes)));
            Check(Wire.ReadText(packet.Reader, TransportDisconnectReasons.MaximumUtf8Bytes) == result,
                "Normalized disconnect reason did not survive strict UTF-8 wire encoding.");
            packet.Complete();
        }
        Throws<ArgumentException>(() => TransportDisconnectReasons.Normalize("invalid\ud800"));
        Throws<ArgumentException>(() => TransportDisconnectReasons.Normalize(new string('x', 201) + "\udc00"));
    }

    private static void ActualDirectCustomKickReasonsDisconnect()
    {
        using var host = new NetworkSession(Options("Reason host"));
        using var guest = new NetworkSession(Options("Reason guest"));
        host.Host(0);
        void Poll() { host.Tick(); guest.Tick(); }
        foreach (string reason in new[] { new string('x', 200), new string('x', 201), new string('x', 256),
            new string('x', 197) + "😀suffix", new string('x', 196) + "😀suffix" })
        {
            guest.Join("127.0.0.1", host.Port);
            Until(() => guest.IsReady && host.Peers.Count == 2, Poll);
            uint peer = guest.LocalPeerId;
            // Rejected reasons must leave the link usable for the accepted kick.
            Throws<ArgumentException>(() => host.Kick(peer, new string('x', 257)));
            Throws<ArgumentException>(() => host.Kick(peer, "invalid\ud800"));
            Check(guest.IsReady && host.Peers.Count == 2, "Invalid reason mutated session membership.");
            host.Kick(peer, reason);
            Until(() => guest.State == SessionState.Offline && host.Peers.Count == 1, Poll);
            string expected = TransportDisconnectReasons.Normalize(reason);
            Check(guest.Status == "Disconnected: " + expected && host.LastPeerEvent.EndsWith(" left: " + expected, StringComparison.Ordinal),
                "Direct kick reason differed between the guest and host summary.");
        }
    }

    private static int Main()
    {
        Test("Shared message bounds and independent reliable stream placement", BoundsAndChannels);
        Test("Actual Direct UDP preserves bytes, message boundaries, world/media ordering and unreliable poses", UdpPreservesBoundariesAndOrderedStreams);
        Test("Actual Direct UDP disposal within Poll suppresses subsequent callbacks", UdpDisposesFromPollWithoutFurtherCallbacks);
        Test("Actual Direct UDP join to an unreachable host reports ConnectionFailed without throwing", UdpUnreachableHostReportsConnectionFailed);
        Test("Direct join to an unresolvable address reports UnknownHost without throwing", UdpUnresolvableAddressReportsUnknownHost);
        Test("Transport startup failures and retired callbacks cleanly release session ownership", StartupFailuresAndRetiredCallbacks);
        Test("Terminal asynchronous failures close hosting/joining and allow a fresh retry (in-memory adapter)", TerminalAsynchronousFailureRetiresBothRoles);
        Test("Shared session protocol preserves host identity, authority, late join and host departure (in-memory adapter)", SharedProtocolKeepsIdentityAndAuthority);
        Test("Actual Direct UDP supports three host/join cycles, late joins, reconnects and host departure", ActualDirectRepeatedSessionsLateJoinReconnectAndDeparture);
        Test("Disconnect reasons preserve UTF-8 byte bounds and Unicode scalar boundaries", DisconnectReasonsKeepUtf8Boundaries);
        Test("Actual Direct custom kick reasons at 200/201/256 UTF-8 bytes disconnect and preserve diagnostics", ActualDirectCustomKickReasonsDisconnect);
        Console.WriteLine($"RESULT passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}
