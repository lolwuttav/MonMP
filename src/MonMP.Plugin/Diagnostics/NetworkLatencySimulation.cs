using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using MonMP.Core.Protocol;
using MonMP.Core.Transport;

namespace MonMP.Plugin.Diagnostics
{
    // Development assembly only. This delays complete application messages, not
    // Steam/LiteNetLib acknowledgements, route discovery or connection callbacks.
    internal sealed class NetworkLatencySettings
    {
        private readonly Func<double> clock, random;
        private bool enabled;
        private int delay, jitter;
        internal NetworkLatencySettings(Func<double>? clock = null, Func<double>? random = null)
        {
            var watch = Stopwatch.StartNew(); var source = new Random();
            this.clock = clock ?? (() => watch.Elapsed.TotalSeconds); this.random = random ?? source.NextDouble;
        }
        internal bool Enabled { get => enabled; set { enabled = value; if (!value) HoldUntil = 0; } }
        internal int DelayMilliseconds { get => delay; set { if (value < 0 || value > 2000) throw new ArgumentOutOfRangeException(nameof(value)); delay = value; } }
        internal int JitterMilliseconds { get => jitter; set { if (value < 0 || value > 500) throw new ArgumentOutOfRangeException(nameof(value)); jitter = value; } }
        internal double Now => clock();
        internal double HoldUntil { get; private set; }
        internal bool Delaying => Enabled && (delay != 0 || jitter != 0 || HoldUntil > Now);
        internal void Hitch(int milliseconds)
        {
            if (milliseconds < 1 || milliseconds > 5000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            Enabled = true; HoldUntil = Math.Max(HoldUntil, Now + milliseconds / 1000d);
        }
        internal void Reset() { Enabled = false; delay = jitter = 0; }
        internal double Due(double now) => Math.Max(HoldUntil, now + Math.Max(0, delay + (random() * 2 - 1) * jitter) / 1000d);
    }

    internal class LatencySimulatedTransport : INetworkTransport
    {
        internal const int MaximumQueuedPackets = 8192, MaximumPacketsPerConnection = 2048;
        internal const long MaximumQueuedBytes = 8 * 1024 * 1024;
        private sealed class Link
        {
            internal bool Closing;
            internal int Pending, ReliableOutgoing;
            internal long ReliableBytes;
            internal readonly double[] OutDue = new double[3], InDue = new double[3];
            internal readonly int[] OutPending = new int[3], InPending = new int[3];
        }
        private sealed class Packet
        {
            internal int Connection;
            internal byte[] Bytes = null!;
            internal bool Reliable;
            internal double Due;
            internal long Sequence;
        }
        private sealed class PacketOrder : IComparer<Packet>
        {
            public int Compare(Packet? a, Packet? b)
            {
                if (ReferenceEquals(a, b)) return 0;
                int time = a!.Due.CompareTo(b!.Due); return time != 0 ? time : a.Sequence.CompareTo(b.Sequence);
            }
        }
        private readonly INetworkTransport inner;
        private readonly NetworkLatencySettings settings;
        private readonly Dictionary<int, Link> links = new Dictionary<int, Link>();
        private readonly SortedSet<Packet> outgoing = new SortedSet<Packet>(new PacketOrder()), incoming = new SortedSet<Packet>(new PacketOrder());
        private long sequence, bytes, reliableOverflows;
        private bool started, terminal;
        protected bool Disposed { get; private set; }
        internal int QueuedPackets => outgoing.Count + incoming.Count;
        internal long QueuedBytes => bytes;
        internal long OverflowDisconnects { get; private set; }
        public TransportKind Kind => inner.Kind;
        public string Description => inner.Description + (settings.Enabled ? " [debug latency simulation]" : "");
        public int LocalPort => inner.LocalPort;
        public long BytesSentPerSecond => inner.BytesSentPerSecond;
        public long BytesReceivedPerSecond => inner.BytesReceivedPerSecond;
        public long ReliableQueueOverflowDisconnects => inner.ReliableQueueOverflowDisconnects + reliableOverflows;
        public event Action<int>? Connected;
        public event Action<int, string>? Disconnected;
        public event Action<int, byte[], bool>? Received;
        public event Action<string>? Error, Failed;

        internal static LatencySimulatedTransport Wrap(INetworkTransport inner, NetworkLatencySettings settings) =>
            inner is IDirectDiscoveryTransport direct ? new DirectLatencyTransport(inner, direct, settings) : new LatencySimulatedTransport(inner, settings);
        private LatencySimulatedTransport(INetworkTransport inner, NetworkLatencySettings settings)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner)); this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            inner.Connected += OnConnected; inner.Disconnected += OnDisconnected; inner.Received += OnReceived;
            inner.Error += OnError; inner.Failed += OnFailed;
        }
        public void Start()
        {
            if (Disposed || started) throw new InvalidOperationException("Latency transport cannot be restarted.");
            started = true; inner.Start();
        }
        public void Poll()
        {
            if (Disposed || terminal) return;
            Drain(outgoing, true);
            if (Disposed || terminal) return;
            inner.Poll();
            if (!Disposed && !terminal) Drain(incoming, false);
        }
        private void OnConnected(int connection)
        {
            if (Disposed || terminal) return;
            Forget(connection); links[connection] = new Link(); Connected?.Invoke(connection);
        }
        private void OnDisconnected(int connection, string reason)
        { if (Disposed || terminal) return; Forget(connection); Disconnected?.Invoke(connection, reason); }
        private void OnError(string reason) { if (!Disposed && !terminal) Error?.Invoke(reason); }
        private void OnFailed(string reason)
        { if (Disposed || terminal) return; terminal = true; Clear(); Failed?.Invoke(reason); }
        private void OnReceived(int connection, byte[] packet, bool reliable)
        {
            if (Disposed || terminal || !links.TryGetValue(connection, out var link) || link.Closing) return;
            if (!settings.Delaying && incoming.Count == 0) { Received?.Invoke(connection, packet, reliable); return; }
            Enqueue(incoming, link, connection, packet, reliable, false);
        }
        public void Send(int connection, byte[] packet, bool reliable)
        {
            TransportMessageRules.ValidateOutgoing(packet);
            if (Disposed || terminal || !links.TryGetValue(connection, out var link) || link.Closing) return;
            if (!settings.Delaying && outgoing.Count == 0) { inner.Send(connection, packet, reliable); return; }
            Enqueue(outgoing, link, connection, packet, reliable, true);
        }
        private void Enqueue(SortedSet<Packet> queue, Link link, int connection, byte[] packet, bool reliable, bool sending)
        {
            if (QueuedPackets >= MaximumQueuedPackets || link.Pending >= MaximumPacketsPerConnection || bytes + packet.Length > MaximumQueuedBytes)
            {
                OverflowDisconnects++; if (reliable) reliableOverflows++;
                Disconnect(connection, "Debug latency simulation queue exceeded its limit; reduce simulated delay and reconnect."); return;
            }
            double now = settings.Now, due = settings.Delaying ? settings.Due(now) : now;
            if (reliable)
            {
                int channel = packet.Length > 6 && packet[6] == (byte)MessageKind.CdMedia ? 2 : 0;
                var lane = sending ? link.OutDue : link.InDue;
                due = Math.Max(due, lane[channel]); lane[channel] = due;
                (sending ? link.OutPending : link.InPending)[channel]++;
            }
            // Queue only owned buffers: a caller/backend may reuse its input immediately.
            queue.Add(new Packet { Connection = connection, Bytes = (byte[])packet.Clone(), Reliable = reliable, Due = due, Sequence = sequence++ });
            bytes += packet.Length; link.Pending++;
            if (sending && reliable) { link.ReliableOutgoing++; link.ReliableBytes += packet.Length; }
        }
        private void Drain(SortedSet<Packet> queue, bool sending)
        {
            while (!Disposed && !terminal && queue.Count != 0)
            {
                double now = settings.Now;
                if (settings.Enabled && settings.HoldUntil > now) return;
                var packet = queue.Min!;
                if (settings.Enabled && packet.Due > now) return;
                queue.Remove(packet); Release(packet, sending);
                if (!links.TryGetValue(packet.Connection, out var link) || link.Closing) continue;
                if (sending) inner.Send(packet.Connection, packet.Bytes, packet.Reliable);
                else Received?.Invoke(packet.Connection, packet.Bytes, packet.Reliable);
            }
        }
        private void Release(Packet packet, bool sending)
        {
            bytes -= packet.Bytes.Length;
            if (!links.TryGetValue(packet.Connection, out var link)) return;
            link.Pending--;
            if (packet.Reliable)
            {
                int channel = packet.Bytes.Length > 6 && packet.Bytes[6] == (byte)MessageKind.CdMedia ? 2 : 0;
                var pending = sending ? link.OutPending : link.InPending;
                if (--pending[channel] == 0) (sending ? link.OutDue : link.InDue)[channel] = 0;
            }
            if (sending && packet.Reliable) { link.ReliableOutgoing--; link.ReliableBytes -= packet.Bytes.Length; }
        }
        private void Forget(int connection)
        {
            outgoing.RemoveWhere(p => { if (p.Connection != connection) return false; Release(p, true); return true; });
            incoming.RemoveWhere(p => { if (p.Connection != connection) return false; Release(p, false); return true; });
            links.Remove(connection);
        }
        public void Disconnect(int connection, string reason)
        {
            reason = TransportDisconnectReasons.Normalize(reason);
            if (Disposed || terminal || !links.TryGetValue(connection, out var link) || link.Closing) return;
            Forget(connection); links[connection] = new Link { Closing = true }; inner.Disconnect(connection, reason);
        }
        // These remain actual backend RTT/loss values. Added application delay is
        // shown separately in the developer UI, not fabricated as native ping.
        public int Ping(int connection) => inner.Ping(connection);
        public TransportPeerMetrics Metrics(int connection)
        {
            var value = inner.Metrics(connection);
            links.TryGetValue(connection, out var link);
            var combined = new TransportPeerMetrics(value.RoundTripTimeMs, value.JitterMs, value.PacketLossPercent,
                value.ReliableQueuePackets + (link?.ReliableOutgoing ?? 0), value.Route, value.RemoteIdentity,
                value.ReliableQueueBytes + (link?.ReliableBytes ?? 0), value.MediaQueuePackets, value.MediaQueueBytes);
            return new TransportPeerMetrics(combined, value.MediaBytesPerSecond);
        }
        private void Clear() { outgoing.Clear(); incoming.Clear(); links.Clear(); bytes = 0; }
        public virtual void Dispose()
        {
            if (Disposed) return;
            Disposed = true; Clear();
            inner.Connected -= OnConnected; inner.Disconnected -= OnDisconnected; inner.Received -= OnReceived;
            inner.Error -= OnError; inner.Failed -= OnFailed; inner.Dispose();
        }
        private sealed class DirectLatencyTransport : LatencySimulatedTransport, IDirectDiscoveryTransport
        {
            private readonly IDirectDiscoveryTransport direct;
            public event Action<IPEndPoint, byte[]>? UnconnectedReceived;
            internal DirectLatencyTransport(INetworkTransport inner, IDirectDiscoveryTransport direct, NetworkLatencySettings settings) : base(inner, settings)
            { this.direct = direct; direct.UnconnectedReceived += Unconnected; }
            private void Unconnected(IPEndPoint endpoint, byte[] packet) { if (!Disposed) UnconnectedReceived?.Invoke(endpoint, packet); }
            public void SendUnconnected(byte[] packet, IPEndPoint endpoint) { if (!Disposed) direct.SendUnconnected(packet, endpoint); }
            public override void Dispose() { direct.UnconnectedReceived -= Unconnected; base.Dispose(); }
        }
    }
}
