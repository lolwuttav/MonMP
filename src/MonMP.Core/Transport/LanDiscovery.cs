using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;

namespace MonMP.Core.Transport
{
    public sealed class LanSession
    {
        public Guid SessionId { get; }
        public string Name { get; }
        public int Port { get; }
        public int Players { get; }
        public bool PasswordRequired { get; }
        public ushort Protocol { get; }
        public string ModVersion { get; }
        public string GameHash { get; }
        public bool Compatible => Protocol == Wire.Version && ModVersion == SessionOptions.CurrentModVersion && GameHash == SessionOptions.SupportedGameHash;
        public LanSession(Guid id, string name, int port, int players, bool passwordRequired, ushort protocol = Wire.Version,
            string modVersion = SessionOptions.CurrentModVersion, string gameHash = SessionOptions.SupportedGameHash)
        {
            Wire.ValidateName(name);
            if (id == Guid.Empty || port < 1 || port > 65535 || players < 1 || protocol == 0 || string.IsNullOrWhiteSpace(modVersion) ||
                modVersion.Length > 32 || gameHash.Length != 64 || gameHash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid LAN session.");
            SessionId = id; Name = name; Port = port; Players = players; PasswordRequired = passwordRequired;
            Protocol = protocol; ModVersion = modVersion; GameHash = gameHash;
        }
    }

    public sealed class DiscoveredHost
    {
        public IPEndPoint Endpoint { get; }
        public LanSession Session { get; }
        public bool SeenOnLan { get; }
        internal double SeenAt { get; }
        internal DiscoveredHost(IPAddress address, LanSession session, double seenAt, int? endpointPort = null, bool seenOnLan = false)
        { Endpoint = new IPEndPoint(address, endpointPort ?? session.Port); Session = session; SeenAt = seenAt; SeenOnLan = seenOnLan; }
    }

    // Separate, opt-in IPv4 LAN socket. Received addresses never come from payloads.
    // No password/profile data, external directory or router change is involved.
    public sealed class LanDiscovery : IDisposable
    {
        public const int DefaultPort = 27021;
        public const int MaxDatagram = 256;
        private const double HostExpirySeconds = 10, QueryLifetimeSeconds = 5, ProbeIntervalSeconds = 2;
        private const int MaxPendingQueries = 512, MaxProbeTargets = 512;
        private const uint Magic = 0x444D4F4D;
        private const byte Schema = 1;
        private sealed class PendingQuery
        {
            public IPAddress? ExpectedAddress;
            public int? ExpectedSourcePort, EndpointPort;
            public double ExpiresAt;
        }
        private readonly Socket socket;
        private readonly NetManager? statusManager;
        private readonly Func<LanSession>? advertise;
        private readonly Func<double> now;
        private readonly Dictionary<string, DiscoveredHost> hosts = new Dictionary<string, DiscoveredHost>();
        private readonly Dictionary<Guid, PendingQuery> pendingQueries = new Dictionary<Guid, PendingQuery>();
        private readonly Dictionary<string, double> lastProbeAt = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly byte[] receive = new byte[MaxDatagram + 1];
        private double searchAt = -100, rateAt = -100;
        private int replies;
        private bool disposed;
        public int LocalPort => ((IPEndPoint)socket.LocalEndPoint!).Port;
        public IReadOnlyCollection<DiscoveredHost> Hosts => hosts.Values;
        public event Action<string>? Error;
        public LanDiscovery(Func<LanSession>? advertise = null, int port = 0, Func<double>? clock = null)
        {
            this.advertise = advertise;
            var watch = Stopwatch.StartNew(); now = clock ?? (() => watch.Elapsed.TotalSeconds);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = true; socket.EnableBroadcast = true; socket.Blocking = false;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch { socket.Dispose(); throw; }
            if (advertise == null)
            {
                try
                {
                    statusManager = CreateStatusManager(ipv6: true) ?? CreateStatusManager(ipv6: false);
                }
                catch { statusManager = null; }
            }
        }
        private NetManager? CreateStatusManager(bool ipv6)
        {
            NetManager? manager = null;
            try
            {
                var listener = new EventBasedNetListener();
                manager = new NetManager(listener)
                { IPv6Enabled = ipv6, UnconnectedMessagesEnabled = true, AutoRecycle = true };
                listener.NetworkReceiveUnconnectedEvent += (endpoint, reader, _) =>
                {
                    if (reader.AvailableBytes < 1 || reader.AvailableBytes > MaxDatagram) return;
                    HandleResponse(reader.GetRemainingBytes(), endpoint, now(), seenOnLan: false);
                };
                if (manager.Start(0)) return manager;
                manager.Stop();
            }
            catch { manager?.Stop(); }
            return null;
        }
        public void Search(IPEndPoint? destination = null)
        {
            if (disposed || advertise != null) throw new InvalidOperationException("Only an active LAN browser can search.");
            if (now() - searchAt < 0.5) return;
            searchAt = now();
            IPAddress? expectedAddress = destination?.Address;
            if (expectedAddress?.Equals(IPAddress.Any) == true || expectedAddress?.Equals(IPAddress.Broadcast) == true)
                expectedAddress = null;
            var target = destination ?? new IPEndPoint(IPAddress.Broadcast, DefaultPort);
            SendQuery(target, new PendingQuery
            { ExpectedAddress = expectedAddress, ExpectedSourcePort = destination?.Port, ExpiresAt = searchAt + QueryLifetimeSeconds });
        }

        /// <summary>Queries the saved gameplay endpoint directly. A valid reply is scoped to its source address and port.</summary>
        public bool Probe(IPEndPoint destination)
        {
            if (disposed || advertise != null || statusManager == null || destination == null || destination.Port < 1 || destination.Port > 65535) return false;
            double time = now();
            string targetKey = destination.Address + ":" + destination.Port;
            if (lastProbeAt.TryGetValue(targetKey, out double last) && time - last < ProbeIntervalSeconds) return false;
            PruneQueries(time);
            if (pendingQueries.Count >= MaxPendingQueries) return false;
            if (!lastProbeAt.ContainsKey(targetKey) && lastProbeAt.Count >= MaxProbeTargets)
            {
                string? oldest = lastProbeAt.OrderBy(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
                if (oldest != null) lastProbeAt.Remove(oldest);
            }
            Guid request = Guid.NewGuid();
            pendingQueries[request] = new PendingQuery
            {
                ExpectedAddress = destination.Address,
                ExpectedSourcePort = destination.Port,
                EndpointPort = destination.Port,
                ExpiresAt = time + QueryLifetimeSeconds
            };
            if (!statusManager.SendUnconnectedMessage(EncodeQuery(request), destination))
            { pendingQueries.Remove(request); return false; }
            lastProbeAt[targetKey] = time;
            return true;
        }

        private void SendQuery(IPEndPoint destination, PendingQuery pending)
        {
            double time = now();
            PruneQueries(time);
            if (pendingQueries.Count >= MaxPendingQueries) return;
            Guid request = Guid.NewGuid();
            pendingQueries[request] = pending;
            Send(EncodeQuery(request), destination);
        }

        private void PruneQueries(double time)
        {
            foreach (Guid request in pendingQueries.Where(pair => time > pair.Value.ExpiresAt).Select(pair => pair.Key).ToArray())
                pendingQueries.Remove(request);
        }
        public void Poll()
        {
            if (disposed) return;
            double time = now();
            foreach (string key in hosts.Where(p => time - p.Value.SeenAt > HostExpirySeconds).Select(p => p.Key).ToArray()) hosts.Remove(key);
            PruneQueries(time);
            statusManager?.PollEvents();
            if (time - rateAt >= 1) { rateAt = time; replies = 0; }
            PollSocket(socket, time);
        }
        private void PollSocket(Socket sourceSocket, double time)
        {
            for (int count = 0; count < 64 && sourceSocket.Poll(0, SelectMode.SelectRead); count++)
            {
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    int length = sourceSocket.ReceiveFrom(receive, ref source);
                    if (length > MaxDatagram) continue;
                    var bytes = new byte[length]; Array.Copy(receive, bytes, length);
                    var endpoint = (IPEndPoint)source;
                    if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.Broadcast) || endpoint.Port < 1) continue;
                    if (advertise != null)
                    {
                        Guid request = DecodeQuery(bytes);
                        if (replies >= 16) continue;
                        replies++; Send(EncodeResponse(request, advertise()), endpoint);
                    }
                    else
                    {
                        HandleResponse(bytes, endpoint, time, seenOnLan: true);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { /* Untrusted/malformed discovery is ignored. */ }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.WouldBlock && ex.SocketErrorCode != SocketError.MessageSize && ex.SocketErrorCode != SocketError.ConnectionReset)
                        Error?.Invoke("LAN discovery: " + ex.SocketErrorCode);
                }
            }
        }
        private void HandleResponse(byte[] bytes, IPEndPoint endpoint, double time, bool seenOnLan)
        {
            try
            {
                var session = DecodeResponse(bytes, out Guid request);
                if (!pendingQueries.TryGetValue(request, out var pending) || time > pending.ExpiresAt) return;
                if (pending.ExpectedAddress != null && !pending.ExpectedAddress.Equals(endpoint.Address)) return;
                if (pending.ExpectedSourcePort is int sourcePort && sourcePort != endpoint.Port) return;
                string key = endpoint.Address + ":" + (pending.EndpointPort ?? session.Port);
                bool wasSeenOnLan = hosts.TryGetValue(key, out var previous) && previous.SeenOnLan;
                if (hosts.ContainsKey(key) || hosts.Count < 256)
                    hosts[key] = new DiscoveredHost(endpoint.Address, session, time, pending.EndpointPort, seenOnLan || wasSeenOnLan);
                if (pending.EndpointPort.HasValue) pendingQueries.Remove(request);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException)
            { /* Untrusted, malformed, and unsolicited status packets are ignored. */ }
        }
        private void Send(byte[] bytes, IPEndPoint destination)
        {
            if (destination.AddressFamily != AddressFamily.InterNetwork) return;
            try { socket.SendTo(bytes, destination); }
            catch (SocketException ex) { Error?.Invoke("LAN discovery: " + ex.SocketErrorCode); }
        }
        private static byte[] Encode(byte kind, Guid request, Action<BinaryWriter> body)
        {
            if (request == Guid.Empty) throw new InvalidDataException("Invalid discovery nonce.");
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Magic); w.Write(Schema); w.Write(kind); w.Write(request.ToByteArray()); body(w);
            if (stream.Length > MaxDatagram) throw new InvalidDataException("LAN datagram too large.");
            return stream.ToArray();
        }
        private static BinaryReader Decode(byte[] bytes, byte kind, out Guid request)
        {
            if (bytes.Length < 22 || bytes.Length > MaxDatagram) throw new InvalidDataException("Invalid discovery length.");
            var r = new BinaryReader(new MemoryStream(bytes, false));
            if (r.ReadUInt32() != Magic || r.ReadByte() != Schema || r.ReadByte() != kind) { r.Dispose(); throw new InvalidDataException("Unknown discovery packet."); }
            request = new Guid(r.ReadBytes(16));
            if (request == Guid.Empty) { r.Dispose(); throw new InvalidDataException("Invalid discovery nonce."); }
            return r;
        }
        public static byte[] EncodeQuery(Guid request) => Encode(1, request, _ => { });
        public static Guid DecodeQuery(byte[] bytes)
        {
            using var r = Decode(bytes, 1, out var request);
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing discovery data.");
            return request;
        }
        public static byte[] EncodeResponse(Guid request, LanSession session) => Encode(2, request, w =>
        {
            w.Write(session.SessionId.ToByteArray()); w.Write(session.Protocol); Wire.WriteText(w, session.ModVersion, 32);
            Wire.WriteText(w, session.GameHash, 64); Wire.WriteText(w, session.Name, Wire.MaxNameBytes);
            w.Write((ushort)session.Port); w.Write(session.Players); w.Write((byte)(session.PasswordRequired ? 1 : 0));
        });
        public static LanSession DecodeResponse(byte[] bytes, Guid expectedRequest)
        {
            var session = DecodeResponse(bytes, out Guid request);
            if (request != expectedRequest) throw new InvalidDataException("Unsolicited discovery response.");
            return session;
        }
        public static LanSession DecodeResponse(byte[] bytes, out Guid request)
        {
            using var r = Decode(bytes, 2, out request);
            var id = new Guid(r.ReadBytes(16)); ushort protocol = r.ReadUInt16();
            string mod = Wire.ReadText(r, 32), game = Wire.ReadText(r, 64), name = Wire.ReadText(r, Wire.MaxNameBytes);
            int port = r.ReadUInt16(), players = r.ReadInt32(); byte locked = r.ReadByte();
            if (locked > 1 || r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Invalid discovery response.");
            return new LanSession(id, name, port, players, locked == 1, protocol, mod, game);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; socket.Dispose(); statusManager?.Stop();
            hosts.Clear(); pendingQueries.Clear(); lastProbeAt.Clear();
        }
    }
}
