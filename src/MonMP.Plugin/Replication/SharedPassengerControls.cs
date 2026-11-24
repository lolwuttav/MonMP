using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // #343: a passenger's native ignition, lighting and gear clicks are intents authorized by
    // the host, then executed by the current driver simulator. The driver lease never changes.
    internal sealed class SharedPassengerControls : IDisposable
    {
        internal static readonly ulong Entity = StaticId.Hash("passenger-vehicle-control-intents/v1");
        private readonly SharedVehicles vehicles;
        private readonly bool host;
        private readonly List<PassengerVehicleIntent> pending = new List<PassengerVehicleIntent>();
        private readonly List<(PassengerVehicleIntent Intent, uint Peer, float At, bool Awake)> candidates =
            new List<(PassengerVehicleIntent, uint, float, bool)>();
        private readonly Dictionary<uint, (float At, int Count)> rates = new Dictionary<uint, (float, int)>();
        private readonly Dictionary<uint, float> acceptedAt = new Dictionary<uint, float>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private uint sequence, seen;
        private float nextApply;
        private PassengerVehicleIntent? heldIgnition;
        private float ignitionStarted;
        private bool applying, disposed;
        private PassengerVehicleIntent? applyingIntent;
        internal int Requested { get; private set; }
        internal int Accepted { get; private set; }
        internal int Applied { get; private set; }
        internal int Acknowledged { get; private set; }
        internal int Rejected { get; private set; }
        internal int ForcedReleases { get; private set; }
        internal int DeferredReceived { get; private set; }
        internal int DeferredPromoted { get; private set; }
        internal int DeferredDropped { get; private set; }
        internal int Pending => pending.Count + candidates.Count;
        internal string LastRejection { get; private set; } = "";
        internal bool AllowsNativeGear(Vehicle_Button_Shifter shifter) => applyingIntent.HasValue &&
            applyingIntent.Value.Control == PassengerVehicleControl.Gear && vehicles.IsPassengerControl(shifter, applyingIntent.Value);
        private bool Live => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true;
        private bool Blocked => !Live || vehicles.TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true;

        internal SharedPassengerControls(SharedVehicles vehicles, bool host) { this.vehicles = vehicles; this.host = host; }
        internal IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Entity, 1, PassengerVehicleControlState.Adapter,
            PassengerVehicleControlState.Encode(Array.Empty<PassengerVehicleIntent>())) };
        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            session.PeerLeft += Left;
        }
        internal bool Request(Component control, string method)
        {
            if (applying || disposed || session?.IsRunning != true) return false;
            var nativeMethod = method == "ChangeLightMode" ? VehicleControlMethod.MouseDown :
                Enum.TryParse(method, out VehicleControlMethod parsed) ? parsed : 0;
            if (!vehicles.TryPassengerIntent(control, nativeMethod, out var intent)) return false;
            // Recognized passenger interactions are always consumed, including while saving.
            if (!Blocked)
            {
                channel!.Request(Entity, intent.EncodeRequest(), true); Requested++;
                vehicles.PassengerPromptRefresh(control);
            }
            return true;
        }
        [MonMP.Core.World.WorldCommandAdapters(PassengerVehicleControlState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Entity || payload.Length == 0) return;
            if (payload[0] == 2)
            {
                uint ack = PassengerVehicleControlState.DecodeAck(payload);
                int index = pending.FindIndex(value => value.Sequence == ack && value.Simulator == peer);
                if (index < 0) return;
                // The stamped simulator alone may ack its exact intent, even if the seat changed
                // after execution. This never authorizes another vehicle action or driver lease.
                pending.RemoveAt(index); acceptedAt.Remove(ack); Acknowledged++; Publish(); return;
            }
            var request = PassengerVehicleIntent.DecodeRequest(payload);
            bool release = request.Control == PassengerVehicleControl.Ignition && request.Method == VehicleControlMethod.MouseUp;
            rates.TryGetValue(peer, out var rate);
            if (Time.unscaledTime - rate.At >= 1) rate = (Time.unscaledTime, 0);
            bool authenticated = vehicles.AuthenticatePassengerIntent(peer, request);
            bool mayWait = !authenticated && vehicles.CanDeferPassengerIntent(peer, request);
            string? rejection = Blocked ? "blocked" : request.Sequence != 0 ? "client sequence must be zero" :
                !authenticated && !mayWait ? "authentication" :
                Pending >= (release ? PassengerVehicleControlState.Capacity : PassengerVehicleControlState.Capacity - 1) ? "queue full" :
                !release && rate.Count >= 8 ? "rate limit" : sequence == uint.MaxValue ? "sequence exhausted" : null;
            if (rejection != null)
            {
                Reject(peer, request, rejection);
                return;
            }
            if (!release) { rate.Count++; rates[peer] = rate; }
            // A fully authenticated MouseUp cannot overtake its deferred MouseDown. Retain
            // received order without assigning a durable execution sequence before acceptance.
            if (mayWait || candidates.Count != 0)
            {
                candidates.Add((request, peer, Time.unscaledTime, vehicles.PassengerIntentAwake(request)));
                DeferredReceived++;
                Debug.Log("MONMP_PASSENGER_CONTROL_DEFER: " + vehicles.PassengerIntentDiagnostic(peer, request));
                return;
            }
            Accept(peer, request);
        }
        private void Reject(uint peer, PassengerVehicleIntent request, string reason)
        {
            Rejected++; LastRejection = reason + ": " + vehicles.PassengerIntentDiagnostic(peer, request);
            if (Rejected <= 8 || Rejected % 100 == 0) Debug.Log("MONMP_PASSENGER_CONTROL_REJECT: " + LastRejection);
        }
        private void Accept(uint peer, PassengerVehicleIntent request)
        {
            var stamped = request.Stamp(++sequence, peer);
            pending.Add(stamped); acceptedAt[stamped.Sequence] = Time.unscaledTime; Accepted++; Publish();
        }
        private void PromoteCandidates()
        {
            while (candidates.Count != 0)
            {
                var candidate = candidates[0]; var request = candidate.Intent;
                bool awake = vehicles.PassengerIntentAwake(request);
                string? drop = Blocked ? "deferred blocked" : vehicles.PassengerDeferralSleeping ? "deferred sleeping" :
                    !vehicles.PassengerIntentCurrent(request) ? "deferred stamp changed" :
                    candidate.Awake && !awake ? "deferred vehicle slept" :
                    Time.unscaledTime - candidate.At > 0.5f ? "deferred pose deadline" :
                    sequence == uint.MaxValue ? "sequence exhausted" : null;
                bool authenticated = drop == null && vehicles.AuthenticatePassengerIntent(candidate.Peer, request);
                if (drop == null && !authenticated && !vehicles.CanDeferPassengerIntent(candidate.Peer, request))
                    drop = "deferred authentication changed";
                if (drop == null && !authenticated)
                {
                    if (awake && !candidate.Awake) candidates[0] = (request, candidate.Peer, candidate.At, true);
                    return;
                }
                candidates.RemoveAt(0);
                if (drop != null) { DeferredDropped++; Reject(candidate.Peer, request, drop); continue; }
                DeferredPromoted++; Accept(candidate.Peer, request);
                Debug.Log("MONMP_PASSENGER_CONTROL_PROMOTED: sequence=" + sequence + "; " + vehicles.PassengerIntentDiagnostic(candidate.Peer, request));
            }
        }
        private void Publish() => channel!.Publish(Entity, PassengerVehicleControlState.Encode(pending));
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(Entity, out var record)) Replace(record);
        }
        private void Changed(WorldRecord record) { if (!host && record.Id == Entity) Replace(record); }
        private void Replace(WorldRecord record)
        {
            var entries = PassengerVehicleControlState.Decode(record.Payload);
            pending.Clear(); pending.AddRange(entries);
        }
        private void Left(uint peer)
        {
            rates.Remove(peer);
            if (heldIgnition.HasValue && (heldIgnition.Value.Actor == peer || heldIgnition.Value.Simulator == peer)) ReleaseIgnition();
            if (!host) return;
            candidates.RemoveAll(value => value.Peer == peer || value.Intent.Simulator == peer);
            foreach (var entry in pending.Where(value => value.Actor == peer || value.Simulator == peer).ToArray())
            { pending.Remove(entry); acceptedAt.Remove(entry.Sequence); }
            Publish();
        }
        internal void Update()
        {
            if (disposed) return;
            if (heldIgnition.HasValue && (Blocked || !vehicles.PassengerIntentCurrent(heldIgnition.Value) ||
                !vehicles.PassengerSimulationLocal(heldIgnition.Value) || !vehicles.PassengerWalkingActorCurrent(heldIgnition.Value) || Time.unscaledTime - ignitionStarted >= 8)) ReleaseIgnition();
            if (!Live) return;
            if (host)
            {
                PromoteCandidates();
                bool changed = false;
                foreach (var entry in pending.ToArray())
                    if (Blocked || !vehicles.PassengerIntentCurrent(entry) ||
                        entry.Walking && !vehicles.AuthenticatePassengerIntent(entry.Actor, entry) ||
                        acceptedAt.TryGetValue(entry.Sequence, out var at) && Time.unscaledTime - at >= 8)
                    { pending.Remove(entry); acceptedAt.Remove(entry.Sequence); changed = true; }
                if (changed) Publish();
            }
            if (Blocked || Time.unscaledTime < nextApply) return;
            // A native light/shift click sets a one-frame input flag. Applying more than one per
            // frame would collapse repeated clicks. Retain the rest until the simulator acks them.
            foreach (var intent in pending.ToArray())
            {
                if (intent.Simulator != session!.LocalPeerId) continue;
                // World commands are reliable and ordered. One ack per execution avoids flooding
                // the transport while a delayed removal delta still contains an executed intent.
                if (intent.Sequence <= seen) continue;
                if (!vehicles.PassengerIntentCurrent(intent) || !vehicles.PassengerSimulationLocal(intent)) continue;
                // Never abandon a previous held key when another actor/vehicle starts cranking.
                if (intent.Control == PassengerVehicleControl.Ignition && intent.Method == VehicleControlMethod.MouseDown && heldIgnition.HasValue &&
                    (heldIgnition.Value.Vehicle != intent.Vehicle || heldIgnition.Value.ControlIndex != intent.ControlIndex || heldIgnition.Value.Actor != intent.Actor))
                    ReleaseIgnition();
                applying = true;
                applyingIntent = intent;
                try { vehicles.ApplyPassengerControl(intent); }
                finally { applyingIntent = null; applying = false; }
                seen = intent.Sequence; Applied++;
                if (intent.Control == PassengerVehicleControl.Ignition)
                {
                    if (intent.Method == VehicleControlMethod.MouseDown) { heldIgnition = intent; ignitionStarted = Time.unscaledTime; }
                    else if (heldIgnition.HasValue && intent.ReleasesIgnition(heldIgnition.Value)) heldIgnition = null;
                }
                channel!.Request(Entity, PassengerVehicleControlState.Ack(intent.Sequence), true);
                nextApply = Time.unscaledTime + 0.08f;
                break;
            }
        }
        private void ReleaseIgnition()
        {
            if (!heldIgnition.HasValue) return;
            var held = heldIgnition.Value; heldIgnition = null;
            applying = true;
            try { vehicles.ApplyPassengerControl(held, release: true); ForcedReleases++; }
            finally { applying = false; }
        }

#if MONMP_DEVELOPMENT
        internal void TestRequest(PassengerVehicleIntent intent) => channel!.Request(Entity, intent.EncodeRequest(), true);


        internal void TestReplayPending() { if (host) Publish(); }
#endif

        public void Dispose()
        {
            if (disposed) return;
            ReleaseIgnition(); disposed = true;
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (session != null) session.PeerLeft -= Left;
            pending.Clear(); candidates.Clear(); acceptedAt.Clear(); rates.Clear();
        }
    }
}
