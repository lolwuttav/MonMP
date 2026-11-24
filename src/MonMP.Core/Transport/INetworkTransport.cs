using System;
using System.Net;
using System.Text;
using MonMP.Core.Protocol;

namespace MonMP.Core.Transport
{
    public enum TransportKind { Direct, Steam }

    // Optional local backend metadata, valid only inside Received. Never derive
    // it from peer-supplied timestamps. Negative means unavailable. Session
    // freshness/authority still uses callback time, independently of rate checks.
    public interface IReceiveTimingTransport
    {
        double CurrentReceiveAgeSeconds { get; }
    }

    public readonly struct TransportPeerMetrics
    {
        public readonly int RoundTripTimeMs, ReliableQueuePackets;
        // Bytes waiting to be submitted/transmitted, when available. Already sent
        // reliable data awaiting acknowledgement is not normal queue pressure.
        public readonly long ReliableQueueBytes;
        // The optional CD media subset of the reliable totals above. Transports
        // with a separate media stream report it so world traffic keeps priority.
        public readonly int MediaQueuePackets;
        public readonly long MediaQueueBytes;
        public readonly double JitterMs, PacketLossPercent;
        private readonly int mediaBudgetPlusOne;
        // -1 means this transport has no adaptive media budget. Zero pauses
        // optional CD traffic; positive values are a per-connection byte rate.
        public int MediaBytesPerSecond => mediaBudgetPlusOne - 1;
        private readonly string? route, remoteIdentity;
        public string Route => route ?? "";
        public string RemoteIdentity => remoteIdentity ?? "";
        public TransportPeerMetrics(int roundTripTimeMs, double jitterMs, double packetLossPercent,
            int reliableQueuePackets, string route = "", string remoteIdentity = "", long reliableQueueBytes = 0,
            int mediaQueuePackets = 0, long mediaQueueBytes = 0)
        {
            RoundTripTimeMs = roundTripTimeMs; JitterMs = jitterMs; PacketLossPercent = packetLossPercent;
            ReliableQueuePackets = reliableQueuePackets; this.route = route; this.remoteIdentity = remoteIdentity;
            ReliableQueueBytes = Math.Max(0, reliableQueueBytes);
            MediaQueuePackets = Math.Max(0, Math.Min(reliableQueuePackets, mediaQueuePackets));
            MediaQueueBytes = Math.Max(0, Math.Min(ReliableQueueBytes, mediaQueueBytes));
            mediaBudgetPlusOne = 0;
        }
        public TransportPeerMetrics(in TransportPeerMetrics metrics, int mediaBytesPerSecond)
        {
            this = metrics;
            mediaBudgetPlusOne = mediaBytesPerSecond < 0 ? 0 : Math.Min(mediaBytesPerSecond, int.MaxValue - 1) + 1;
        }
    }

    // One instance belongs to one session and is configured for hosting or joining
    // before Start. Start may begin asynchronous connection establishment. Every
    // application event must be delivered only by Poll on its caller's thread.
    // Dispose must be idempotent, including from inside a Poll callback, and must
    // suppress later callbacks. Connection IDs are transport-local handles: the
    // host's existing Hello/Welcome exchange assigns the gameplay player IDs.
    public interface INetworkTransport : IDisposable
    {
        TransportKind Kind { get; }
        string Description { get; }
        int LocalPort { get; }
        long BytesSentPerSecond { get; }
        long BytesReceivedPerSecond { get; }
        long ReliableQueueOverflowDisconnects { get; }
        event Action<int>? Connected;
        event Action<int, string>? Disconnected;
        event Action<int, byte[], bool>? Received;
        event Action<string>? Error;
        // A terminal asynchronous failure closes the entire session, including
        // lobby creation failures where no connection ID has existed yet.
        event Action<string>? Failed;
        void Start();
        void Poll();
        int Ping(int connection);
        TransportPeerMetrics Metrics(int connection);
        // One call is one complete Wire message. Reliable messages must be ordered
        // independently on world/control and CD media streams; unreliable messages
        // are individual, unordered datagrams. Never combine message boundaries.
        // Reliable messages may use backend fragmentation up to Wire's 1024-byte
        // bound. Pose datagrams remain below the backend MTU; never make a large
        // unreliable message reliable merely to work around its datagram limit.
        // Copy bytes before returning if the backend retains their managed buffer.
        void Send(int connection, byte[] bytes, bool reliable);
        // Reasons use the shared UTF-8 byte limit, truncating only between Unicode
        // scalar values. Invalid UTF-16 is rejected before changing link state.
        void Disconnect(int connection, string reason);
    }

    // LAN discovery belongs only to Direct IP; Steam lobby metadata is separate.
    public interface IDirectDiscoveryTransport
    {
        event Action<IPEndPoint, byte[]>? UnconnectedReceived;
        void SendUnconnected(byte[] bytes, IPEndPoint endpoint);
    }

    public static class TransportDisconnectReasons
    {
        public const int MaximumUtf8Bytes = 200;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Normalize(string reason) => Normalize(reason, MaximumUtf8Bytes);

        public static string Normalize(string reason, int maximumUtf8Bytes)
        {
            if (reason == null) throw new ArgumentNullException(nameof(reason));
            if (maximumUtf8Bytes < 1 || maximumUtf8Bytes > MaximumUtf8Bytes) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
            // Validate the entire reason, including any suffix that will be
            // truncated, before a transport marks the connection as closing.
            if (Utf8.GetByteCount(reason) <= maximumUtf8Bytes) return reason;
            Span<byte> bytes = stackalloc byte[maximumUtf8Bytes];
            Utf8.GetEncoder().Convert(reason.AsSpan(), bytes, true, out int characters, out _, out _);
            return reason.Substring(0, characters);
        }
    }

    public static class TransportMessageRules
    {
        public const byte WorldReliableChannel = 0, CdMediaReliableChannel = 2;
        public const int MinimumMessageBytes = 23, MaximumMessageBytes = Wire.MaxPacketBytes;

        public static void ValidateOutgoing(byte[] bytes)
        {
            if (bytes == null || bytes.Length < MinimumMessageBytes || bytes.Length > MaximumMessageBytes)
                throw new ArgumentException("Transport message must contain 23 to " + MaximumMessageBytes + " bytes.", nameof(bytes));
        }

        public static byte ChannelFor(byte[] bytes, bool reliable)
        {
            ValidateOutgoing(bytes);
            return reliable && bytes[6] == (byte)MessageKind.CdMedia ? CdMediaReliableChannel : WorldReliableChannel;
        }

        public static bool ValidIncoming(byte[] bytes, bool reliable, int channel)
        {
            if (bytes == null || bytes.Length < MinimumMessageBytes || bytes.Length > MaximumMessageBytes) return false;
            if (!reliable) return channel == WorldReliableChannel;
            return channel == (bytes[6] == (byte)MessageKind.CdMedia ? CdMediaReliableChannel : WorldReliableChannel);
        }
    }
}
