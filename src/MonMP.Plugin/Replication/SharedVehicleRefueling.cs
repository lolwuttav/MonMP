using System;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        internal void RecordRefuel(VehicleController vehicle, float amount)
        {
            var b = Find(vehicle);
            if (!host || !Ready || b == null || b.Dormant || b.State == null || IsLocal(b.State) ||
                !float.IsFinite(amount) || amount <= 0) return;
            var tank = vehicle.vehicle_PartsManager.Fuel_Tank;
            b.State = b.State.WithFuel(tank.Get_FuelLevel, b.State.RefuelCredit + amount);
            Publish(b);
        }

        private VehicleState ReconcileDriverFuel(Binding b, VehicleState reported)
        {
            // Frames sent before the driver saw the refill still contain the old tank level.
            // Retain precisely the host credit they have yet to apply, alongside native burn.
            float outstanding = (float)Math.Max(0, b.State.RefuelCredit - b.RefuelAcknowledged);
            return reported.WithFuel(Mathf.Clamp(reported.Fuel + outstanding, 0,
                b.Vehicle.vehicle_PartsManager.Fuel_Tank.Get_FuelCapacity), b.State.RefuelCredit);
        }

        private void AcknowledgeRefuel(Binding b, uint peer, VehicleRefuelAcknowledgement acknowledgement)
        {
            if (!host || !drivers.Owns(b.Id, peer, acknowledgement.Generation) ||
                acknowledgement.Credit < b.RefuelAcknowledged || acknowledgement.Credit > b.State.RefuelCredit) return;
            b.RefuelAcknowledged = acknowledgement.Credit;
        }

        private void ApplyRefuelCredit(Binding b, double previousCredit, bool local, bool ownerChanged, bool bodyApplied)
        {
            var state = b.State;
            if (host)
            {
                // A newly granted driver starts from the absolute tank level in that grant.
                if (ownerChanged) b.RefuelAcknowledged = state.RefuelCredit;
                return;
            }
            if (!local) return;
            if (!bodyApplied && state.RefuelCredit > previousCredit)
            {
                var tank = b.Vehicle.vehicle_PartsManager.Fuel_Tank;
                tank.Get_FuelLevel = Mathf.Clamp(tank.Get_FuelLevel + (float)(state.RefuelCredit - previousCredit), 0, tank.Get_FuelCapacity);
            }
            if (ownerChanged || state.RefuelCredit > b.RefuelAcknowledged)
            {
                channel!.Request(b.Id, new VehicleRefuelAcknowledgement(state.Driver.Generation, state.RefuelCredit).Encode(), true);
                b.RefuelAcknowledged = state.RefuelCredit;
            }
        }
    }
}
