using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The selected host's native 21-entry statistics array is the shared saved total.
    // Needs, health timers, clothes and player placement remain personal.
    internal sealed class SharedStatistics : IDisposable
    {
        private readonly bool host;
        private readonly float[] original;
        private readonly float[] capture = new float[SharedStatisticsState.Count];
        private readonly SharedStatisticsHost authority;
        private readonly SharedStatisticsClient replica = new SharedStatisticsClient();
        private readonly HashSet<AchievementType> checkedAwards = new HashSet<AchievementType>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextUpdate, nextRetry, submittedAt;
        private uint lastRevision;
        private bool disposed;
        private SharedStatisticsState lastPublished;
        public bool Active => !disposed && session?.IsRunning == true;
        // Continue reliable retries, but optional statistics cannot wedge native save
        // readiness for the session after an unavailable host handler.
        public bool LocalFlushComplete => host || !replica.Initialized || replica.Settled ||
            (replica.InFlight != null && Time.unscaledTime - submittedAt >= 30f);
        public SharedStatistics(bool host)
        {
            this.host = host; original = (float[])Capture().Clone(); authority = new SharedStatisticsHost(original);
            lastPublished = authority.State;
        }
        private float[] Capture()
        {
            var native = GameManager.Get_PlayerStats;
            if (native == null || native.Length != SharedStatisticsState.Count)
                throw new InvalidOperationException("Unsupported native statistics layout.");
            for (int i = 0; i < native.Length; i++)
            {
                float value = native[i];
                capture[i] = float.IsNaN(value) || float.IsInfinity(value) || value < 0 ? 0 : value;
            }
            return capture;
        }
        private static void Write(IReadOnlyList<float> values)
        { for (int i = 0; i < values.Count; i++) GameManager.Get_PlayerStats[i] = values[i]; }
        public IEnumerable<WorldRecord> InitialRecords()
        { yield return new WorldRecord(SharedStatisticsState.Entity, 1, SharedStatisticsState.Adapter, authority.State.Encode()); }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.ReplyReceived += Reply;
            session.PeerLeft += PeerLeft;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!host && records.TryGetValue(SharedStatisticsState.Entity, out var record)) Apply(record); }
        private void Changed(WorldRecord record)
        { if (!host && record.Id == SharedStatisticsState.Entity && record.Adapter == SharedStatisticsState.Adapter) Apply(record); }
        private void Apply(WorldRecord record)
        {
            if (session == null || disposed) return;
            if (lastRevision != 0 && record.Revision != lastRevision && unchecked((int)(record.Revision - lastRevision)) <= 0) return;
            var state = SharedStatisticsState.Decode(record.Payload);
            replica.ObserveNative(Capture()); replica.Apply(state, session.LocalPeerId);
            Write(replica.SynchronizePresentation()); lastRevision = record.Revision;
        }
        [WorldCommandAdapters(SharedStatisticsState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || !Active || channel == null || entity != SharedStatisticsState.Entity ||
                peer == 0 || session?.Peers.ContainsKey(peer) != true) return;
            var contribution = StatisticsContribution.Decode(payload);
            authority.ObserveNative(Capture().AsSpan()); authority.Accept(peer, contribution);
            Write(authority.State.Values); Publish();
            // A retry must recover its acknowledgement even when the retained state
            // is unchanged and Publish therefore produces no new delta.
            var current = authority.State;
            var state = current.Encode();
            if (4 + state.Length > 128)
            {
                // The retained world record keeps every peer's acknowledgement. A
                // direct reply only needs this requester's receipt to settle its batch.
                var acknowledged = current.Acknowledged.TryGetValue(peer, out ulong sequence)
                    ? new[] { new KeyValuePair<uint, ulong>(peer, sequence) }
                    : Array.Empty<KeyValuePair<uint, ulong>>();
                state = new SharedStatisticsState(current.Values, acknowledged).Encode();
            }
            var reply = new byte[4 + state.Length];
            BitConverter.GetBytes(channel.Records[entity].Revision).CopyTo(reply, 0); state.CopyTo(reply, 4);
            channel.Reply(peer, entity, reply);
        }
        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != SharedStatisticsState.Entity || payload.Length < 4) return;
            var state = payload.Skip(4).ToArray();
            Apply(new WorldRecord(entity, BitConverter.ToUInt32(payload, 0), SharedStatisticsState.Adapter, state));
        }
        private void PeerLeft(uint peer)
        {
            if (!host) return;
            authority.ObserveNative(Capture().AsSpan()); authority.RemovePeer(peer); Publish();
        }
        private void Publish()
        {
            if (channel == null || !host) return;
            var state = authority.State;
            if (ReferenceEquals(state, lastPublished)) return;
            channel.Publish(SharedStatisticsState.Entity, state.Encode()); lastPublished = state;
        }
        public void Update()
        {
            if (!Active || channel == null || Time.unscaledTime < nextUpdate) return;
            nextUpdate = Time.unscaledTime + 0.25f;
            if (host)
            {
                authority.ObserveNative(Capture().AsSpan()); Write(authority.State.Values); Publish();
            }
            else if (replica.Initialized)
            {
                replica.ObserveNative(Capture()); Write(replica.SynchronizePresentation());
                if (channel.FullySynchronized)
                {
                    var fresh = replica.BeginContribution();
                    if (fresh != null) submittedAt = Time.unscaledTime;
                    var request = fresh ?? (Time.unscaledTime >= nextRetry ? replica.InFlight : null);
                    if (request != null)
                    {
                        channel.Request(SharedStatisticsState.Entity, request.Encode(), true);
                        nextRetry = Time.unscaledTime + 1f;
                    }
                }
            }
            CheckAwards();
        }
        private void CheckAwards()
        {
            if (GameManager.SETTING_Cheats || (!host && !replica.Initialized)) return;
            var manager = Singleton<Steam_AchievementManager>.i;
            if (manager == null) return;
            var values = GameManager.Get_PlayerStats;
            void Award(int index, float threshold, AchievementType achievement)
            {
                if (values[index] < threshold || checkedAwards.Contains(achievement)) return;
                manager.AchievementDone(achievement); checkedAwards.Add(achievement);
            }
            Award(0, 50, AchievementType.PoutineEaten_50); Award(0, 250, AchievementType.PoutineEaten_250);
            Award(1, 50, AchievementType.SyrupDrank_50); Award(1, 250, AchievementType.SyrupDrank_250);
            Award(9, 1, AchievementType.Lumberjack_1); Award(9, 50, AchievementType.Lumberjack_50); Award(9, 250, AchievementType.Lumberjack_250);
            Award(10, 50, AchievementType.PlantHarvested_50); Award(10, 250, AchievementType.PlantHarvested_250);
            Award(13, 100, AchievementType.Crink_100); Award(15, 1, AchievementType.FirstPoliceTicket);
            Award(3, 100000000, AchievementType.CashMade_OneMillion); Award(11, 1, AchievementType.DieForTheFirstTime);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (session != null) session.PeerLeft -= PeerLeft;
            if (!host) Write(original);
            session = null; channel = null;
        }
    }
}
