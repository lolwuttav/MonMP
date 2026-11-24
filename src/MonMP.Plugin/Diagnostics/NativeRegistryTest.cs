using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal static class NativeRegistryTest
    {
        internal static void Verify(SharedWorld world)
        {
                var nativeItems = world.Entities.Snapshot().Where(i => i.AdapterType == ItemState.Adapter).ToArray();
                Require(nativeItems.Length > 0, "Replicated item registry is empty.");
                foreach (var identity in nativeItems)
                    Require(identity.AdapterType == ItemState.Adapter && !identity.IsStaticSceneEntity &&
                        world.Entities.Resolve(identity.EntityId) == identity && identity.SpawnKey == identity.GetComponent<InteractableItems>().PrefabName,
                        "Native item identity does not resolve to its spawn recipe.");
                var staticRecords = world.Channel.Records.Values.Where(r => r.Adapter == 1 || r.Adapter == VehicleState.Adapter || r.Adapter == BoatMotionState.Adapter ||
                    r.Adapter == VehiclePassengerSeatState.Adapter || r.Adapter == VehicleSeatAdjustmentState.Adapter ||
                    r.Adapter == VehicleDoorState.Adapter).ToArray();
                int lights = staticRecords.Count(r => r.Adapter == 1), vehicles = staticRecords.Count(r => r.Adapter == VehicleState.Adapter),
                    passengers = staticRecords.Count(r => r.Adapter == VehiclePassengerSeatState.Adapter),
                    adjustments = staticRecords.Count(r => r.Adapter == VehicleSeatAdjustmentState.Adapter),
                    doors = staticRecords.Count(r => r.Adapter == VehicleDoorState.Adapter);
                // Minimum coverage: the starting Konig, ATV, truck and scrapyard Konig, plus any later-bound vehicles.
                // Every bound Konig variant contributes its own native passenger seat.
                // Derive that count from the vehicle bindings, independently of the channel registry.
                int nativePassengers = world.Vehicles.InitialRecords().Count(r => r.Adapter == VehiclePassengerSeatState.Adapter);
                int expectedPassengers = nativePassengers + world.SchoolBusPassengers.SeatCount + world.ExtraSeats.Count;
                Require(lights > 0 && vehicles >= 4 && nativePassengers > 0 && passengers == expectedPassengers &&
                    adjustments >= 2 && staticRecords.Count(r => r.Adapter == BoatMotionState.Adapter) == 2 && doors >= 4, "Expected light/vehicle/boat/seat/door registry coverage is missing: lights=" + lights +
                    " vehicles=" + vehicles + " passengers=" + passengers + "/" + expectedPassengers +
                    " adjustments=" + adjustments + " doors=" + doors + ".");
                foreach (var record in staticRecords)
                {
                    var identity = world.Entities.Resolve(record.Id);
                    Require(identity != null && identity.IsStaticSceneEntity && identity.AdapterType == record.Adapter &&
                        (record.Adapter == 1 ? identity.GetComponent<Furniture_Light>() != null :
                         record.Adapter == VehicleState.Adapter ? identity.GetComponent<NWH.VehiclePhysics2.VehicleController>() != null :
                         record.Adapter == BoatMotionState.Adapter ? identity.GetComponent<NWH.DWP2.ShipController.AdvancedShipController>() != null :
                         record.Adapter == VehiclePassengerSeatState.Adapter ? identity.GetComponent<Konig_Seat>() != null || identity.GetComponent<Vehicle_PassengerSeat>() != null || world.ExtraSeats.Contains(record.Id) :
                         record.Adapter == VehicleSeatAdjustmentState.Adapter ? identity.GetComponent<Konig_Seat>() != null :
                         identity.GetComponent<Vehicle_Door>() != null),
                        "Static world record does not resolve to the matching native component.");
                }
                var collisionTarget = new GameObject("MonMPRegistryTest");
                try
                {
                    var firstIdentity = nativeItems[0];
                    Reject(() => world.Entities.Bind(firstIdentity.EntityId, ItemState.Adapter, collisionTarget, firstIdentity.SpawnKey, false));
                    Reject(() => world.Entities.Bind(firstIdentity.EntityId + 1, ItemState.Adapter, firstIdentity.gameObject, firstIdentity.SpawnKey, false));
                    using var isolated = new NativeEntityRegistry();
                    var owned = isolated.Bind(1, ItemState.Adapter, collisionTarget, "fixture", true);
                    Require(isolated.Bind(1, ItemState.Adapter, collisionTarget, "fixture", true) == owned, "Idempotent registration changed identity.");
                    var role = isolated.Bind(2, VehicleDoorState.Adapter, collisionTarget, "other-role", true);
                    Require(isolated.Resolve(2) == role && role != owned, "Distinct roles on one native object were not represented separately.");
                    Reject(() => isolated.Bind(3, ItemState.Adapter, collisionTarget, "fixture", true));
                    isolated.Dispose();
                    Require(isolated.Count == 0 && isolated.Resolve(1) == null && collisionTarget != null && owned.Registry == null,
                        "Registry disposal retained bindings or destroyed a native object.");
                }
                finally { UnityEngine.Object.Destroy(collisionTarget); }
        }
        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Native registry: " + message); }
        private static void Reject(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Native registry accepted an identity collision."); }
    }
}
