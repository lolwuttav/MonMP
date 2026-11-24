using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using LiteNetLib;
using MonMP.Core.Protocol;

namespace MonMP.Core.Transport
{
    // All application callbacks are dispatched by Poll on its caller's thread.
    public sealed class LiteNetTransport : INetworkTransport, IDirectDiscoveryTransport
    {
        private sealed class LatencySample { public int LastRoundTrip; public double Jitter; public bool Started; }
        public const string ConnectionKey = "MonMP/direct/12";
        private readonly NetManager manager;
        private readonly Dictionary<int, NetPeer> peers = new Dictionary<int, NetPeer>();
        private readonly Dictionary<int, LatencySample> latency = new Dictionary<int, LatencySample>();
        private readonly object sendGate = new object();
        private readonly Stopwatch trafficClock = Stopwatch.StartNew();
        private long previousSent, previousReceived;
        private long reliableQueueOverflowDisconnects;
        private double previousTrafficTime;
        private readonly int configuredPort, connectPort;
        private readonly string? connectAddress;
        private bool started, disposed;
        public event Action<int>? Connected;
        public event Action<int, string>? Disconnected;
        public event Action<int, byte[], bool>? Received;
        public event Action<IPEndPoint, byte[]>? UnconnectedReceived;
        public event Action<string>? Error;
        // UDP startup failures throw synchronously; peer errors use Disconnected.
        public event Action<string>? Failed { add { } remove { } }
        public TransportKind Kind => TransportKind.Direct;
        public string Description => "Direct IP / LAN (LiteNetLib UDP)";
        public int LocalPort => manager.LocalPort;
        public long BytesSentPerSecond { get; private set; }
        public long BytesReceivedPerSecond { get; private set; }
        public long ReliableQueueOverflowDisconnects => Interlocked.Read(ref reliableQueueOverflowDisconnects);

        private const int MaxConnections = Session.NetworkSession.MaxGuests + 8;
        private const byte WorldReliableChannel = TransportMessageRules.WorldReliableChannel,
            CdMediaReliableChannel = TransportMessageRules.CdMediaReliableChannel;

        public LiteNetTransport(bool host, bool ipv6)
            : this(host, ipv6, 0) { }

        public LiteNetTransport(bool host, bool ipv6, int localPort, string? address = null, int remotePort = 0)
        {
            if (localPort < 0 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (address != null && (host || string.IsNullOrWhiteSpace(address) || remotePort < 1 || remotePort > 65535))
                throw new ArgumentException("A joining transport needs a host address and port.", nameof(address));
            configuredPort = localPort; connectAddress = address; connectPort = remotePort;
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener)
            {
                IPv6Enabled = ipv6, ChannelsCount = 3, AutoRecycle = true,
                DisconnectTimeout = 5000, MaxConnectAttempts = 8, ReconnectDelay = 500,
                EnableStatistics = true, UnconnectedMessagesEnabled = host
            };
            listener.ConnectionRequestEvent += request =>
            {
                // Bounded so unauthenticated handshakes cannot pile up before their timeout; room for
                // every admitted guest plus a few joining at once.
                if (host && manager.ConnectedPeersCount < MaxConnections) request.AcceptIfKey(ConnectionKey);
                else request.Reject();
            };
            listener.PeerConnectedEvent += peer => { if (disposed) return; peers.Add(peer.Id, peer); Connected?.Invoke(peer.Id); };
            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (disposed) return;
                // LiteNetLib reports a failed outgoing connect (host unreachable, port not
                // forwarded) without a peer; still deliver its reason instead of throwing.
                int id = peer?.Id ?? -1;
                peers.Remove(id); latency.Remove(id);
                string reason = info.Reason.ToString();
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0 && info.AdditionalData.AvailableBytes <= Wire.MaxPacketBytes)
                {
                    try
                    {
                        using var packet = Wire.Decode(info.AdditionalData.GetRemainingBytes());
                        if (packet.Kind == MessageKind.Reject)
                        {
                            reason = Wire.ReadText(packet.Reader, TransportDisconnectReasons.MaximumUtf8Bytes);
                            packet.Complete();
                        }
                    }
                    catch (Exception) { reason = "Malformed disconnect reason."; }
                }
                Disconnected?.Invoke(id, reason);
            };
            listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
            {
                if (disposed) return;
                if (reader.AvailableBytes > Wire.MaxPacketBytes)
                {
                    Disconnect(peer.Id, "Packet too large.");
                    return;
                }
                bool reliable = delivery == DeliveryMethod.ReliableOrdered && (channel == 0 || channel == 2);
                // LiteNetLib does not retain a channel number on Unreliable packets.
                if (!reliable && delivery != DeliveryMethod.Unreliable)
                {
                    Disconnect(peer.Id, "Unsupported delivery mode.");
                    return;
                }
                byte[] bytes = reader.GetRemainingBytes();
                // Preserve the low-level diagnostic path for truncated packets:
                // NetworkSession's bounded Wire parser supplies its established
                // malformed-handshake status. Channel placement is still checked.
                bool media = bytes.Length > 6 && bytes[6] == (byte)MessageKind.CdMedia;
                if (reliable && media != (channel == CdMediaReliableChannel))
                {
                    Disconnect(peer.Id, "Message on incorrect reliable channel.");
                    return;
                }
                Received?.Invoke(peer.Id, bytes, reliable);
            };
            if (host)
                listener.NetworkReceiveUnconnectedEvent += (endpoint, reader, _) =>
                {
                    if (disposed) return;
                    if (reader.AvailableBytes < 1 || reader.AvailableBytes > LanDiscovery.MaxDatagram) return;
                    UnconnectedReceived?.Invoke(endpoint, reader.GetRemainingBytes());
                };
            listener.NetworkErrorEvent += (_, error) => { if (!disposed) Error?.Invoke("UDP error: " + error); };
        }

        public void Start()
        {
            Start(configuredPort);
            if (connectAddress != null) Connect(connectAddress, connectPort);
        }
        public void Start(int port)
        {
            ThrowIfDisposed();
            if (started) throw new InvalidOperationException("Transport is already started.");
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (!manager.Start(port)) throw new InvalidOperationException("Could not bind UDP port " + port + ".");
            started = true;
        }
        public void Connect(string address, int port)
        {
            ThrowIfDisposed();
            if (!started) throw new InvalidOperationException("Start the transport before connecting.");
            manager.Connect(address, port, ConnectionKey);
        }
        public void SendUnconnected(byte[] bytes, IPEndPoint endpoint)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > LanDiscovery.MaxDatagram)
                throw new ArgumentException("Invalid unconnected datagram.", nameof(bytes));
            manager.SendUnconnectedMessage(bytes, endpoint);
        }
        public void Poll()
        {
            if (disposed || !started) return;
            manager.PollEvents();
            if (disposed) return;
            double now = trafficClock.Elapsed.TotalSeconds;
            if (now - previousTrafficTime < 1) return;
            double elapsed = Math.Max(now - previousTrafficTime, 0.001);
            long sent = manager.Statistics.BytesSent, received = manager.Statistics.BytesReceived;
            BytesSentPerSecond = Math.Max(0, (long)((sent - previousSent) / elapsed));
            BytesReceivedPerSecond = Math.Max(0, (long)((received - previousReceived) / elapsed));
            previousSent = sent; previousReceived = received; previousTrafficTime = now;
            foreach (var pair in peers)
            {
                int rtt = pair.Value.RoundTripTime;
                if (!latency.TryGetValue(pair.Key, out var sample)) latency.Add(pair.Key, sample = new LatencySample());
                if (sample.Started) sample.Jitter += (Math.Abs(rtt - sample.LastRoundTrip) - sample.Jitter) * 0.25;
                sample.LastRoundTrip = rtt; sample.Started = true;
            }
        }
        public int Ping(int id) => peers.TryGetValue(id, out var peer) ? peer.Ping : 0;
        public TransportPeerMetrics Metrics(int id)
        {
            if (!peers.TryGetValue(id, out var peer)) return default;
            double jitter = latency.TryGetValue(id, out var sample) ? sample.Jitter : 0;
            return new TransportPeerMetrics(peer.RoundTripTime, jitter, peer.Statistics.PacketLossPercent,
                ReliableQueuePackets(peer), "Direct UDP", peer.ToString(), 0,
                (int)Math.Min(int.MaxValue, peer.GetPacketsCountInReliableQueue(CdMediaReliableChannel, true)));
        }
        void INetworkTransport.Send(int id, byte[] bytes, bool reliable)
        {
            TransportMessageRules.ValidateOutgoing(bytes);
            Send(id, bytes, reliable);
        }

        // The original low-level API remains available to protocol diagnostics
        // which deliberately send malformed envelopes. Sessions use the checked
        // INetworkTransport entry point above.
        public void Send(int id, byte[] bytes, bool reliable)
        {
            if (bytes == null || bytes.Length > Wire.MaxPacketBytes)
                throw new ArgumentException("Transport message exceeds the wire size limit.", nameof(bytes));
            lock (sendGate)
            {
                if (!peers.TryGetValue(id, out var peer) || peer.ConnectionState != ConnectionState.Connected) return;

                // Media has its own ordered stream, so one lost audio chunk cannot hold up
                // clock, structural state or snapshot packets on the world stream.
                byte channel = reliable && bytes.Length > 6 && bytes[6] == (byte)MessageKind.CdMedia ?
                    CdMediaReliableChannel : WorldReliableChannel;
                if (reliable)
                {
                    int charge = ReliableSendQueuePolicy.PacketCharge(bytes.Length,
                        peer.GetMaxSinglePacketSize(DeliveryMethod.ReliableOrdered));
                    int queued = ReliableQueuePackets(peer);
                    if (ReliableSendQueuePolicy.ExceedsLimit(queued, charge))
                    {
                        Interlocked.Increment(ref reliableQueueOverflowDisconnects);
                        string reason = "Reliable transport backlog exceeded " +
                            ReliableSendQueuePolicy.HardLimitPacketsPerPeer +
                            " queued packets; reconnect to synchronize the latest world state.";
                        peer.Disconnect(Wire.Encode(MessageKind.Reject, Guid.Empty, w =>
                            Wire.WriteText(w, reason, TransportDisconnectReasons.MaximumUtf8Bytes)));
                        return;
                    }
                }
                peer.Send(bytes, channel, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            }
        }
        public void Disconnect(int id, string reason)
        {
            reason = TransportDisconnectReasons.Normalize(reason);
            lock (sendGate)
                if (peers.TryGetValue(id, out var peer))
                    peer.Disconnect(Wire.Encode(MessageKind.Reject, Guid.Empty, w =>
                        Wire.WriteText(w, reason, TransportDisconnectReasons.MaximumUtf8Bytes)));
        }
        public void Dispose()
        {
            lock (sendGate)
            {
                if (disposed) return;
                disposed = true;
                manager.Stop(); peers.Clear(); latency.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LiteNetTransport));
        }

        private static int ReliableQueuePackets(NetPeer peer)
        {
            // Both application reliable ordered streams share one peer budget.
            // The byte-channel overload is public in the pinned LiteNetLib 2.1.4 API.
            long count = (long)peer.GetPacketsCountInReliableQueue(0, true) +
                peer.GetPacketsCountInReliableQueue(CdMediaReliableChannel, true);
            return count >= int.MaxValue ? int.MaxValue : (int)count;
        }
    }
}
