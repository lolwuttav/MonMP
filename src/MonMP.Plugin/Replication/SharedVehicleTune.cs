using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Guest vehicles felt badly underpowered: the part-install record carries only prefabs, so a guest's
    // ECU kept its own save's or a fresh clone's tune. The turbo's gain scales with the ECU boost (1.6x at
    // the default 10/20, up to 2.2x tuned) and the power curve comes from the tune's six points. The
    // nitrous charge had the same gap. The host owns both: it publishes its copy; a guest who tunes the
    // ECU, or burns nitrous while driving, requests the new values and the host applies them natively.
    internal sealed class SharedVehicleTune : IDisposable
    {
        public const ushort Adapter = VehicleTuneState.Adapter;
        private const float HostInterval = 0.25f, GuestInterval = 0.25f, ScanInterval = 2f, PendingSeconds = 3f;
        private static readonly MethodInfo? UpdateEcuMethod = AccessTools.Method(typeof(Vehicle_PartsManager), "Update_ECU");
        private static readonly HashSet<string> writeFailures = new HashSet<string>();

        private sealed class Binding
        {
            public ulong Id;
            public VehicleController Vehicle = null!;
            public byte[] Original = null!;
            // The parts the last record was applied to: a different instance is a swapped part that
            // takes the record, not a local tune to send.
            public InteractableParts? AppliedEcu, AppliedBottle;
            public VehicleTuneState Applied;
            public bool HasApplied;
            public float HostCharge;
            public bool ChargeApplied;
            public VehicleTuneState Sent;
            public float SentAt = -999f;
        }
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly bool host;
        private readonly SharedVehicles vehicles;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextTick, nextScan;
        public int Count => bindings.Count;

        public SharedVehicleTune(bool host, SharedVehicles vehicles)
        {
            this.host = host; this.vehicles = vehicles;
            writeFailures.Clear();
            // SharedVehicles is already bound here, before any snapshot callbacks can replace
            // the guest's ECU/bottle. Keep the native baseline rather than a later prefab clone.
            Scan();
        }

        internal static ulong RecordId(ulong vehicle) =>
            WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(vehicle.ToString("X16") + "/vehicle-tune-v1") & 0x7FFFFFFFFFFFFFFFUL);

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            // Attach precedes NetworkSession.Host. Register captured baselines from
            // the ready Update scan, once the session is allowed to register records.
        }

        private static Vehicle_PartSocket? EcuSocket(VehicleController v) =>
            v.vehicle_type == Vehicle_Type.OlTruck ? null : v.vehicle_PartsManager?.ECU;
        private static Vehicle_PartSocket? BottleSocket(VehicleController v) => v.vehicle_PartsManager?.NitrousBottle;
        private static Part_ECU? Ecu(VehicleController v)
        {
            var socket = EcuSocket(v);
            return socket != null && socket.isInstalled && socket.partInstalled != null ? socket.partInstalled.GetComponent<Part_ECU>() : null;
        }
        private static InteractableParts? Bottle(VehicleController v)
        {
            var socket = BottleSocket(v);
            return socket != null && socket.isInstalled && socket.partInstalled != null ? socket.partInstalled : null;
        }

        private static VehicleTuneState Capture(VehicleController v)
        {
            var ecu = Ecu(v); var bottle = Bottle(v);
            bool hasEcu = ecu != null && VehicleTuneState.IsValidEcu(ecu.IntValue1, ecu.IntValue2);
            float charge = bottle == null ? 0 : Mathf.Clamp(float.IsNaN(bottle.Value1) ? 0 : bottle.Value1, 0, 1000);
            return new VehicleTuneState(hasEcu, hasEcu ? ecu!.IntValue1 : 0, hasEcu ? ecu!.IntValue2 : 0, bottle != null, charge);
        }

        private void Scan()
        {
            foreach (var v in vehicles.BoundControllers)
            {
                if (v == null || v.vehicle_PartsManager == null || (EcuSocket(v) == null && BottleSocket(v) == null)) continue;
                ulong vehicle = vehicles.EntityOf(v);
                if (vehicle == 0) continue;
                ulong id = RecordId(vehicle);
                if (bindings.TryGetValue(id, out var existing) && existing.Vehicle == v)
                {
                    if (host && channel != null && !channel.Records.ContainsKey(id))
                        channel.Register(new WorldRecord(id, 1, Adapter, existing.Original));
                    continue;
                }
                var b = new Binding { Id = id, Vehicle = v, Original = Capture(v).Encode() };
                bindings[id] = b;
                if (channel == null) continue;
                if (host) { if (!channel.Records.ContainsKey(id)) channel.Register(new WorldRecord(id, 1, Adapter, b.Original)); }
                else if (channel.Records.TryGetValue(id, out var record)) Apply(b, record.Payload);
            }
        }

        [MonMP.Core.World.WorldCommandAdapters(VehicleTuneState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || b.Vehicle == null ||
                !VehicleTuneState.TryDecode(payload, out var wanted)) return;
            var current = Capture(b.Vehicle);
            if (wanted.HasEcu != current.HasEcu || wanted.HasBottle != current.HasBottle) return;
            // A native refill can happen between the host's capture cadence and a driver's debit.
            // That debit still carries the last published revision, so protect the unpublished fill
            // and publish it immediately rather than letting a stale lower charge erase it.
            bool unpublishedRefill = current.HasBottle && channel!.Records.TryGetValue(b.Id, out var record) &&
                VehicleTuneState.TryDecode(record.Payload, out var published) && published.HasBottle && current.Charge > published.Charge;
            var next = current;
            // Anyone may retune the ECU (the native window has no owner); the bottle drains only for
            // the vehicle's driver, and never fills here (the refill station runs on the host).
            if (wanted.HasEcu && (wanted.Ecu1 != current.Ecu1 || wanted.Ecu2 != current.Ecu2)) next = next.WithEcu(wanted.Ecu1, wanted.Ecu2);
            if (wanted.HasBottle && !unpublishedRefill && vehicles.DrivenBy(b.Vehicle) == peer && wanted.Charge < current.Charge) next = next.WithCharge(wanted.Charge);
            if (next.Equals(current) && !unpublishedRefill) return;
            Write(b.Vehicle, next, current);
            channel!.Publish(b.Id, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b)) return;
            Apply(b, record.Payload);
        }

        // A guest applies the host's values unless it is the one changing them: its own pending tune,
        // or the charge while it drives (the host's copy lags its own consumption).
        private void Apply(Binding b, byte[] payload)
        {
            if (b.Vehicle == null || !VehicleTuneState.TryDecode(payload, out var state)) return;
            var local = Capture(b.Vehicle);
            bool pending = Time.unscaledTime - b.SentAt < PendingSeconds;
            if (pending && state.HasEcu && local.HasEcu && (state.Ecu1 != b.Sent.Ecu1 || state.Ecu2 != b.Sent.Ecu2)) state = state.WithEcu(local.Ecu1, local.Ecu2);
            else if (pending) b.SentAt = -999f;
            // Preserve only consumption of the same, already synchronized bottle. The first record,
            // a newly installed bottle and a host refill must seed the driver's charge too; otherwise
            // a guest entering before tune discovery keeps its empty/default bottle forever.
            bool sameBottle = b.ChargeApplied && Bottle(b.Vehicle) == b.AppliedBottle;
            // Applied is also the optimistic command baseline: an older consumption acknowledgement
            // can exceed it without being a refill. Compare only consecutive host records instead.
            bool refill = sameBottle && state.HasBottle && b.Applied.HasBottle && state.Charge > b.HostCharge;
            b.HostCharge = state.Charge;
            if (state.HasBottle && local.HasBottle && DrivingHere(b) && sameBottle && !refill)
                state = state.WithCharge(Mathf.Min(state.Charge, local.Charge));
            // Part records can arrive in different orders: an absent ECU must not prevent a present
            // bottle from taking its host charge (and vice versa). Write checks each socket itself.
            Write(b.Vehicle, state, local);
            b.ChargeApplied = state.HasBottle && local.HasBottle;
            b.Applied = Capture(b.Vehicle);
            b.AppliedEcu = Ecu(b.Vehicle); b.AppliedBottle = Bottle(b.Vehicle); b.HasApplied = true;
        }

        private bool DrivingHere(Binding b) => session != null && vehicles.DrivenBy(b.Vehicle) == session.LocalPeerId;

        private static void Write(VehicleController v, VehicleTuneState state, VehicleTuneState current)
        {
            try
            {
                if (state.HasEcu && (state.Ecu1 != current.Ecu1 || state.Ecu2 != current.Ecu2) && Ecu(v) is Part_ECU ecu)
                {
                    ecu.IntValue1 = state.Ecu1; ecu.IntValue2 = state.Ecu2;
                    ecu.RefreshCurve();
                    UpdateEcuMethod?.Invoke(v.vehicle_PartsManager, null);
                }
            }
            catch (Exception ex) { ReportWriteFailure(v, "ECU", ex); }
            try
            {
                if (state.HasBottle && !state.Charge.Equals(current.Charge) && Bottle(v) is InteractableParts bottle)
                {
                    bottle.Value1 = Mathf.Min(state.Charge, bottle.Value1_Max);
                    try { v.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>()?.module?.UpdateGauge(); } catch (Exception) { }
                }
            }
            catch (Exception ex) { ReportWriteFailure(v, "nitrous bottle", ex); }
        }
        private static void ReportWriteFailure(VehicleController vehicle, string component, Exception exception)
        {
            if (writeFailures.Add(vehicle.GetInstanceID() + "/" + component))
                Debug.LogWarning("MonMP vehicle tune could not apply " + component + " on " + vehicle.vehicle_type +
                    ": " + exception.GetType().Name + ": " + exception.Message);
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true) return;
            if (Time.unscaledTime >= nextScan && WorldScans.TryClaim()) { nextScan = Time.unscaledTime + ScanInterval; Scan(); }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + (host ? HostInterval : GuestInterval);
            foreach (var b in bindings.Values)
            {
                if (b.Vehicle == null) continue;
                var local = Capture(b.Vehicle);
                if (host)
                {
                    if (!channel.Records.TryGetValue(b.Id, out var record)) continue;
                    if (!VehicleTuneState.TryDecode(record.Payload, out var published) || !published.Equals(local)) channel.Publish(b.Id, local.Encode());
                    continue;
                }
                if (!b.HasApplied || !channel.Records.TryGetValue(b.Id, out var hostRecord)) continue;
                // A part swapped in since the last record takes the record instead of reporting its own values.
                if (Ecu(b.Vehicle) != b.AppliedEcu || Bottle(b.Vehicle) != b.AppliedBottle)
                { b.SentAt = -999f; Apply(b, hostRecord.Payload); continue; }
                if (local.HasEcu != b.Applied.HasEcu || local.HasBottle != b.Applied.HasBottle) continue;
                bool tuned = local.HasEcu && (local.Ecu1 != b.Applied.Ecu1 || local.Ecu2 != b.Applied.Ecu2);
                // Applied includes optimistic local edits. An older/conflicting host record may
                // have been preserved during that edit's window, so reconcile when the window
                // ends even if no further revision arrives. A newer native edit gets its own
                // request below rather than being overwritten by this reconciliation.
                if (!tuned && b.SentAt >= 0f && Time.unscaledTime - b.SentAt >= PendingSeconds)
                {
                    b.SentAt = -999f;
                    Apply(b, hostRecord.Payload);
                    local = Capture(b.Vehicle);
                }
                // Applying a host record can preserve a local debit before the reporting cadence
                // runs. Applied then contains that debit already, so it cannot be its send baseline.
                // Keep comparing to the last host charge until the host acknowledges consumption.
                bool burned = local.HasBottle && DrivingHere(b) && b.ChargeApplied && Bottle(b.Vehicle) == b.AppliedBottle &&
                    (b.HostCharge - local.Charge > 0.002f || (local.Charge <= 0 && b.HostCharge > 0));
                if (!tuned && !burned) continue;
                var request = b.Applied;
                if (tuned) request = request.WithEcu(local.Ecu1, local.Ecu2);
                if (burned) request = request.WithCharge(local.Charge);
                channel.Request(b.Id, request.Encode());
                if (tuned) { b.Sent = request; b.SentAt = Time.unscaledTime; }
                b.Applied = request;
            }
        }


#if MONMP_DEVELOPMENT
        internal bool TestBound(VehicleController vehicle) { foreach (var b in bindings.Values) if (b.Vehicle == vehicle) return true; return false; }


        internal VehicleTuneState TestOriginal(VehicleController vehicle) =>
            VehicleTuneState.Decode(bindings.Values.Single(b => b.Vehicle == vehicle).Original);


        internal bool TestChargeSeeded(VehicleController vehicle) => bindings.Values.Any(b => b.Vehicle == vehicle && b.ChargeApplied && Bottle(vehicle) == b.AppliedBottle);


        internal void TestReapplyHostRecord(VehicleController vehicle)
        {
            var b = bindings.Values.Single(value => value.Vehicle == vehicle);
            Apply(b, channel!.Records[b.Id].Payload);
        }


        internal void TestStaleDriverDebitAfterRefill(VehicleController vehicle, uint peer)
        {
            var b = bindings.Values.Single(value => value.Vehicle == vehicle);
            var published = VehicleTuneState.Decode(channel!.Records[b.Id].Payload);
            var native = Capture(vehicle);
            if (!host || vehicles.DrivenBy(vehicle) != peer || !native.HasBottle || !published.HasBottle || native.Charge <= published.Charge)
                throw new InvalidOperationException("NOS stale-debit fixture requires an authenticated driver and unpublished native refill.");
            Command(peer, b.Id, native.WithCharge(published.Charge).Encode());
            if (Capture(vehicle).Charge != native.Charge || VehicleTuneState.Decode(channel.Records[b.Id].Payload).Charge != native.Charge)
                throw new InvalidOperationException("Stale driver debit erased the unpublished host refill or failed to publish it.");
        }
#endif


        public void Dispose()
        {
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            // A guest's own save keeps its own tune and charge.
            if (!host)
                foreach (var b in bindings.Values)
                    if (b.Vehicle != null && VehicleTuneState.TryDecode(b.Original, out var original))
                    {
                        var local = Capture(b.Vehicle);
                        // Write independently checks the restored ECU and bottle sockets. An absent
                        // bottle must not suppress restoration of an existing ECU (or vice versa).
                        Write(b.Vehicle, original, local);
                    }
            bindings.Clear(); session = null; channel = null;
        }
    }
}
