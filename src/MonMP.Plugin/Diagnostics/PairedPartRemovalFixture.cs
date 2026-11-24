using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Tightening fixtures construct native bolt objects. Clear that fixture stock on the host
    // only after the guest has observed full tightening; physical wrench use has its own suite.
    internal sealed class PairedPartRemovalFixture : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly WorldChannel channel;
        private readonly HashSet<ulong> requested = new HashSet<ulong>();
        private readonly HashSet<ulong> cleared = new HashSet<ulong>();
        private float nextRequest;
        internal PairedPartRemovalFixture(MonMPPlugin plugin)
        {
            this.plugin = plugin; channel = plugin.World!.Channel;
            if (plugin.TestOptions.IsHost) channel.CommandRequested += Requested;
        }
        private void Requested(uint peer, ulong entity, byte[] payload)
        {
            if (peer != 0 && entity == CottagePurchaseState.Entity && payload.Length == 10 && payload[0] == 0x72 && payload[1] == 0x62)
                requested.Add(BitConverter.ToUInt64(payload, 2));
        }
        internal void Request(ulong socket)
        {
            if (Time.unscaledTime < nextRequest) return;
            var payload = new byte[10]; payload[0] = 0x72; payload[1] = 0x62;
            Array.Copy(BitConverter.GetBytes(socket), 0, payload, 2, 8);
            channel.Request(CottagePurchaseState.Entity, payload, true); nextRequest = Time.unscaledTime + 0.5f;
        }
        internal void HostClear(SharedVehicleParts parts, ulong id)
        {
            if (!requested.Contains(id) || !cleared.Add(id)) return;
            var socket = parts.SocketOf(id) ?? throw new InvalidOperationException("Part removal fixture lost its native socket.");
            if (socket.boltHolesDetails.Length == 0) return;
            if (!parts.TestNative(id).Installed || !parts.TestNative(id).FullyTightened)
                throw new InvalidOperationException("Part removal fixture did not preserve the fully tightened host part before unbolting.");
            foreach (var hole in socket.boltHolesDetails)
                if (hole.isBoltInstalled) hole.DetachAndDestroy();
            // Native bolt removal publishes the independent hole records. It does not publish
            // a whole-part edit; the structural source check already tolerates bolt progress.
            Debug.Log("MONMP_PART_REMOVAL_FIXTURE: host cleared " + socket.boltHolesDetails.Length + " fixture bolts after guest tightening acknowledgement; socket=" + id.ToString("X"));
        }
        internal bool GuestReady(SharedVehicleParts parts, ulong id)
        {
            Request(id);
            var socket = parts.SocketOf(id) ?? throw new InvalidOperationException("Part removal fixture lost its native socket.");
            var bolts = plugin.World!.Bolts;
            var holes = bolts.TestFirstHoles(bolts.Count, socket.vehicleAttached.Get_Vehicle.vehicle_type)
                .Where(hole => bolts.TestHole(hole).SockedBoltedByThisBolt == socket).ToArray();
            if (holes.Length != socket.boltHolesDetails.Length)
                throw new InvalidOperationException("Part removal fixture lacks shared records for every native socket bolt.");
            if (socket.boltHolesDetails.Any(hole => hole.isBoltInstalled) || holes.Any(hole => bolts.TestRecord(hole).Installed)) return false;
            var accepted = parts.TestRecord(id); var native = parts.TestNative(id);
            if (!native.Installed || !accepted.Equals(new VehiclePartState(native.Installed, native.Prefab, accepted.FullyTightened,
                native.Value1, native.Value2, native.IntValue1, native.IntValue2))) return false;
            if (!socket.CheckIfCanToRemoved())
                throw new InvalidOperationException("Native removal remains blocked after all fixture bolts were cleared: " + parts.TestSocketType(id));
            return true;
        }
        public void Dispose() => channel.CommandRequested -= Requested;
    }
}
