using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;

namespace MonMP.Core.World
{
    public sealed class SharedStatisticsState
    {
        public const ushort Adapter = 154;
        public static readonly ulong Entity = StaticId.Hash("shared-player-statistics/v1");
        public const int Count = PlayerProfileState.StatCount, MaximumPeers = 32, MaximumIndex = 17;
        public IReadOnlyList<float> Values { get; }
        public IReadOnlyDictionary<uint, ulong> Acknowledged { get; }
        public SharedStatisticsState(IEnumerable<float> values, IEnumerable<KeyValuePair<uint, ulong>>? acknowledged = null)
        {
            var data = Validate(values);
            var acks = new Dictionary<uint, ulong>();
            foreach (var entry in acknowledged ?? Array.Empty<KeyValuePair<uint, ulong>>())
            {
                if (entry.Key == 0 || entry.Value == 0 || acks.ContainsKey(entry.Key) || acks.Count >= MaximumPeers)
                    throw new InvalidDataException("Invalid shared-statistics acknowledgement.");
                acks.Add(entry.Key, entry.Value);
            }
            Values = Array.AsReadOnly(data); Acknowledged = new ReadOnlyDictionary<uint, ulong>(acks);
        }
        internal static float[] Validate(IEnumerable<float> values)
        {
            var data = values.ToArray();
            if (data.Length != Count || data.Any(v => float.IsNaN(v) || float.IsInfinity(v) || v < 0))
                throw new InvalidDataException("Invalid shared-statistics values.");
            return data;
        }
        internal static float Add(float left, float right) => (float)Math.Min(float.MaxValue, (double)left + right);
        public static bool HostOwns(int index) => index == 3 || index == 4 || index == 5 || index == 6 || index == 9 || index == 10 ||
            index == 13 || index == 14 || index == 15 || index == 16 || index == 18 || index == 19 || index == 20;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            foreach (float value in Values) writer.Write(value);
            writer.Write((ushort)Acknowledged.Count);
            foreach (var entry in Acknowledged.OrderBy(value => value.Key)) { writer.Write(entry.Key); writer.Write(entry.Value); }
            return stream.ToArray();
        }
        public static SharedStatisticsState Decode(byte[] payload)
        {
            const int header = Count * 4 + 2;
            if (payload == null || payload.Length < header || payload.Length > header + MaximumPeers * 12)
                throw new InvalidDataException("Invalid shared-statistics payload length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var data = new float[Count]; for (int i = 0; i < Count; i++) data[i] = reader.ReadSingle();
            int count = reader.ReadUInt16();
            if (count > MaximumPeers || payload.Length != header + count * 12) throw new InvalidDataException("Invalid statistics acknowledgement count.");
            var acks = new List<KeyValuePair<uint, ulong>>();
            for (int i = 0; i < count; i++) acks.Add(new KeyValuePair<uint, ulong>(reader.ReadUInt32(), reader.ReadUInt64()));
            return new SharedStatisticsState(data, acks);
        }
    }

    public sealed class StatisticsContribution
    {
        public const int Size = 8 + SharedStatisticsState.Count * 4;
        public ulong Sequence { get; }
        public IReadOnlyList<float> Values { get; }
        public StatisticsContribution(ulong sequence, IEnumerable<float> values)
        {
            if (sequence == 0) throw new InvalidDataException("Invalid statistics contribution sequence.");
            Sequence = sequence; Values = Array.AsReadOnly(SharedStatisticsState.Validate(values));
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Sequence); foreach (float value in Values) writer.Write(value); return stream.ToArray();
        }
        public static StatisticsContribution Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid statistics contribution length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            ulong sequence = reader.ReadUInt64(); var data = new float[SharedStatisticsState.Count];
            for (int i = 0; i < data.Length; i++) data[i] = reader.ReadSingle();
            return new StatisticsContribution(sequence, data);
        }
    }

    // Native host actions already mutate these totals. Guest cash playback never contributes again.
    public sealed class SharedStatisticsHost
    {
        private readonly float[] values;
        private readonly Dictionary<uint, ulong> accepted = new Dictionary<uint, ulong>();
        private SharedStatisticsState? cachedState;
        public SharedStatisticsHost(IEnumerable<float> native) { values = SharedStatisticsState.Validate(native); }
        public void ObserveNative(IEnumerable<float> native) => ObserveNative(SharedStatisticsState.Validate(native).AsSpan());
        public void ObserveNative(ReadOnlySpan<float> native)
        {
            if (native.Length != values.Length) throw new InvalidDataException("Invalid shared-statistics values.");
            bool changed = false;
            for (int i = 0; i < native.Length; i++)
            {
                if (float.IsNaN(native[i]) || float.IsInfinity(native[i]) || native[i] < 0)
                    throw new InvalidDataException("Invalid shared-statistics values.");
                // Encoding preserves the sign of zero, so equality must preserve it too.
                changed |= BitConverter.SingleToInt32Bits(values[i]) != BitConverter.SingleToInt32Bits(native[i]);
            }
            // Validate the complete input before changing any totals or cached snapshot.
            if (!changed) return;
            native.CopyTo(values); cachedState = null;
        }
        public SharedStatisticsState State => cachedState ??= new SharedStatisticsState(values, accepted);
        public bool Accept(uint peer, StatisticsContribution contribution)
        {
            if (peer == 0) throw new InvalidDataException("Host statistics cannot be a guest contribution.");
            if (accepted.TryGetValue(peer, out ulong previous) && contribution.Sequence <= previous) return false;
            if (!accepted.ContainsKey(peer) && accepted.Count >= SharedStatisticsState.MaximumPeers)
                throw new InvalidDataException("Too many statistics contributors.");
            for (int i = 0; i < values.Length; i++)
            {
                if (SharedStatisticsState.HostOwns(i)) continue; // Host world/native transactions already count these actions.
                values[i] = i == SharedStatisticsState.MaximumIndex ? Math.Max(values[i], contribution.Values[i]) :
                    SharedStatisticsState.Add(values[i], contribution.Values[i]);
            }
            accepted[peer] = contribution.Sequence;
            cachedState = null; // A new acknowledgement matters even when every total is unchanged.
            return true;
        }
        public void RemovePeer(uint peer) { if (accepted.Remove(peer)) cachedState = null; }
    }

    // Pending action increments survive unrelated host snapshots; their accepted batch is subtracted once.
    public sealed class SharedStatisticsClient
    {
        private readonly float[] pending = new float[SharedStatisticsState.Count];
        private float[] observed = new float[SharedStatisticsState.Count], authoritative = new float[SharedStatisticsState.Count];
        private ulong sequence;
        public bool Initialized { get; private set; }
        public StatisticsContribution? InFlight { get; private set; }
        public bool Settled => InFlight == null && !pending.Any(v => v > 0);
        public void ObserveNative(IEnumerable<float> native)
        {
            var data = SharedStatisticsState.Validate(native);
            if (Initialized)
                for (int i = 0; i < data.Length; i++)
                {
                    if (SharedStatisticsState.HostOwns(i) || data[i] <= observed[i]) continue;
                    pending[i] = i == SharedStatisticsState.MaximumIndex ? Math.Max(pending[i], data[i]) :
                        SharedStatisticsState.Add(pending[i], data[i] - observed[i]);
                }
            observed = data;
        }
        public void Apply(SharedStatisticsState state, uint localPeer)
        {
            if (InFlight != null && state.Acknowledged.TryGetValue(localPeer, out ulong ack) && ack >= InFlight.Sequence)
            {
                for (int i = 0; i < pending.Length; i++)
                    pending[i] = i == SharedStatisticsState.MaximumIndex ?
                        (pending[i] <= InFlight.Values[i] ? 0 : pending[i]) : Math.Max(0, pending[i] - InFlight.Values[i]);
                InFlight = null;
            }
            authoritative = state.Values.ToArray(); Initialized = true;
            observed = Visible();
        }
        public float[] Visible()
        {
            var result = new float[pending.Length];
            for (int i = 0; i < result.Length; i++) result[i] = i == SharedStatisticsState.MaximumIndex ?
                Math.Max(authoritative[i], pending[i]) : SharedStatisticsState.Add(authoritative[i], pending[i]);
            return result;
        }
        public float[] SynchronizePresentation()
        {
            var result = Visible(); observed = result.ToArray(); return result;
        }
        public StatisticsContribution? BeginContribution()
        {
            if (!Initialized || InFlight != null || Settled) return null;
            InFlight = new StatisticsContribution(++sequence, pending);
            return InFlight;
        }
    }
}
