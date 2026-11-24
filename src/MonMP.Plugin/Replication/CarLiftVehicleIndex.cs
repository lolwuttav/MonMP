using System.Collections.Generic;
using HarmonyLib;
using NWH;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Membership belongs to this shared world. Read the native freeze reference live:
    // caching that value would delay a freeze/unfreeze or accept the wrong vehicle.
    internal sealed class CarLiftVehicleIndex
    {
        private static readonly AccessTools.FieldRef<Furniture_CarLift, Vehicle> FrozenVehicle =
            AccessTools.FieldRefAccess<Furniture_CarLift, Vehicle>("Cache_VehicleFrozen");
        private readonly List<Furniture_CarLift> lifts = new List<Furniture_CarLift>();

        internal CarLiftVehicleIndex(IEnumerable<Furniture_CarLift> initial)
        {
            foreach (var lift in initial) Observe(lift);
        }

        // Native freeze/unfreeze also observes a newly bought or spawned lift before
        // the next vehicle update. No periodic scene rediscovery is necessary.
        internal void Observe(Furniture_CarLift lift)
        {
            if (lift == null || !lift.gameObject.scene.IsValid()) return;
            for (int i = lifts.Count - 1; i >= 0; i--)
            {
                if (lifts[i] == null) lifts.RemoveAt(i);
                else if (ReferenceEquals(lifts[i], lift)) return;
            }
            lifts.Add(lift);
        }

        internal bool Contains(Vehicle vehicle)
        {
            if (vehicle == null) return false;
            for (int i = lifts.Count - 1; i >= 0; i--)
            {
                var lift = lifts[i];
                if (lift == null) { lifts.RemoveAt(i); continue; }
                // FindObjectsOfType's old active-object semantics include disabled
                // components, but exclude inactive GameObjects.
                if (lift.gameObject.activeInHierarchy && FrozenVehicle(lift) == vehicle) return true;
            }
            return false;
        }

        internal void Clear() => lifts.Clear();
    }
}
